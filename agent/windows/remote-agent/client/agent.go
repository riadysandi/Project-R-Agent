package client

import (
	"bufio"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/gorilla/websocket"
	"github.com/project-r/windows-remote-agent/input"
	"github.com/project-r/windows-remote-agent/screen"
)

type Config struct {
	ServerURL string
	DeviceID  string
	Token     string
	FPS       int
	Quality   int
}

type Agent struct {
	cfg             Config
	capturer        *screen.Capturer
	conn            *websocket.Conn
	connMu          sync.Mutex
	streaming       atomic.Bool
	stopChan        chan struct{}
	fps             int32
	waitingAck      int32
	lastSentTime    time.Time
	sessionCmd      *exec.Cmd
	sessionStdin    io.WriteCloser
	sessionMu       sync.Mutex
	consentApproved atomic.Bool
}

func NewAgent(cfg Config) *Agent {
	log.Println("[NewAgent] Step 1: Normalizing FPS/Quality...")
	if cfg.FPS <= 0 {
		cfg.FPS = 25
	}
	if cfg.Quality <= 0 {
		cfg.Quality = 55
	}

	log.Println("[NewAgent] Step 2: Calling screen.NewCapturer...")
	capt := screen.NewCapturer(cfg.Quality)
	log.Println("[NewAgent] Step 3: screen.NewCapturer done.")

	return &Agent{
		cfg:      cfg,
		capturer: capt,
		stopChan: make(chan struct{}),
		fps:      int32(cfg.FPS),
	}
}

// Start begins the reverse WebSocket connection and screen streaming loop.
func (a *Agent) Start() {
	log.Printf("[Agent] Starting Project R Remote Desktop Agent for Device [%s]...", a.cfg.DeviceID)

	for {
		select {
		case <-a.stopChan:
			log.Println("[Agent] stopChan triggered in a.Start(). Returning.")
			return
		default:
			log.Println("[Agent] Calling connectAndRun()...")
			err := a.connectAndRun()
			if err != nil {
				log.Printf("[Agent] Connection error: %v. Reconnecting in 5 seconds...", err)
			}
			time.Sleep(5 * time.Second)
		}
	}
}

// Stop gracefully stops the agent.
func (a *Agent) Stop() {
	close(a.stopChan)
	a.connMu.Lock()
	if a.conn != nil {
		_ = a.conn.Close()
	}
	a.connMu.Unlock()
}

func (a *Agent) connectAndRun() error {
	serverURL := a.cfg.ServerURL
	// Convert http/https to ws/wss if needed
	if strings.HasPrefix(serverURL, "http://") {
		serverURL = "ws://" + serverURL[7:]
	} else if strings.HasPrefix(serverURL, "https://") {
		serverURL = "wss://" + serverURL[8:]
	}

	u, err := url.Parse(serverURL)
	if err != nil {
		return fmt.Errorf("invalid server URL: %w", err)
	}

	// Clean path and ensure it points to /remote/ws/agent
	cleanPath := strings.TrimRight(u.Path, "/")
	cleanPath = strings.TrimSuffix(cleanPath, "/api/agent")
	cleanPath = strings.TrimSuffix(cleanPath, "/panel")
	cleanPath = strings.TrimSuffix(cleanPath, "/remote/ws/agent")
	cleanPath = strings.TrimSuffix(cleanPath, "/remote/ws")
	cleanPath = strings.TrimSuffix(cleanPath, "/ws/agent")
	cleanPath = strings.TrimSuffix(cleanPath, "/ws")

	u.Path = cleanPath + "/remote/ws/agent"

	q := u.Query()
	q.Set("device_id", a.cfg.DeviceID)
	q.Set("token", a.cfg.Token)
	u.RawQuery = q.Encode()

	dialer := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
		TLSClientConfig:  &tls.Config{InsecureSkipVerify: true}, // Allow custom enterprise certs
	}

	log.Printf("[Agent] Dialing relay server: %s", u.String())
	conn, resp, err := dialer.Dial(u.String(), nil)
	if err != nil {
		if resp != nil {
			return fmt.Errorf("dial failed with status %d: %w", resp.StatusCode, err)
		}
		return fmt.Errorf("dial failed: %w", err)
	}

	a.connMu.Lock()
	a.conn = conn
	a.connMu.Unlock()

	defer func() {
		screen.PreventDisplaySleep(false)
		a.connMu.Lock()
		if a.conn != nil {
			_ = a.conn.Close()
			a.conn = nil
		}
		a.connMu.Unlock()
	}()

	log.Printf("[Agent] Connected successfully to Project R Remote Relay!")

	// Keep display active while connected
	screen.PreventDisplaySleep(true)

	// Send initial screen resolution & device specs (with physical & capture dimensions)
	cw, ch, pw, ph := a.capturer.GetDimensions()
	initMsg := map[string]interface{}{
		"type": "init",
		"payload": map[string]interface{}{
			"device_id":       a.cfg.DeviceID,
			"screen_width":    cw,
			"screen_height":   ch,
			"physical_width":  pw,
			"physical_height": ph,
			"version":         "1.0.0",
		},
	}
	initBytes, _ := json.Marshal(initMsg)
	_ = conn.WriteMessage(websocket.TextMessage, initBytes)

	// Only send initial full keyframe and start streaming if consent is approved or not required
	if !a.requireConsent() || a.consentApproved.Load() {
		if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
			atomic.StoreInt32(&a.waitingAck, 1)
			a.lastSentTime = time.Now()
			_ = conn.WriteMessage(websocket.BinaryMessage, packet)
		}
		a.streaming.Store(true)
	} else {
		// Waiting for user approval on client desktop
		a.streaming.Store(false)
	}

	// Goroutine: Screen capture & frame transmission
	doneChan := make(chan struct{})
	go a.captureLoop(doneChan)

	// Main loop: Read incoming input commands from viewers
	for {
		msgType, data, err := conn.ReadMessage()
		if err != nil {
			close(doneChan)
			return err
		}

		if msgType == websocket.TextMessage {
			a.handleCommand(data)
		}
	}
}

func (a *Agent) captureLoop(doneChan <-chan struct{}) {
	for {
		select {
		case <-doneChan:
			return
		case <-a.stopChan:
			return
		default:
			// If not streaming (no viewers watching), sleep to use 0% CPU
			if !a.streaming.Load() {
				time.Sleep(250 * time.Millisecond)
				continue
			}

			// Flow control: if viewer hasn't acknowledged previous frame and <150ms has elapsed,
			// skip encoding this frame to prevent buffering delay and lag buildup
			if atomic.LoadInt32(&a.waitingAck) == 1 && time.Since(a.lastSentTime) < 150*time.Millisecond {
				time.Sleep(10 * time.Millisecond)
				continue
			}

			targetFPS := atomic.LoadInt32(&a.fps)
			if targetFPS <= 0 {
				targetFPS = 25
			}
			interval := time.Duration(1000/targetFPS) * time.Millisecond
			start := time.Now()

			// Capture frame
			packet, err := a.capturer.CaptureFrame(false)
			if err != nil {
				time.Sleep(interval)
				continue
			}

			if packet != nil {
				a.connMu.Lock()
				if a.conn != nil {
					atomic.StoreInt32(&a.waitingAck, 1)
					a.lastSentTime = time.Now()
					_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
				}
				a.connMu.Unlock()
			}

			elapsed := time.Since(start)
			if elapsed < interval {
				time.Sleep(interval - elapsed)
			}
		}
	}
}

func (a *Agent) mapCoords(x, y int) (int, int) {
	cw, ch, pw, ph := a.capturer.GetDimensions()
	if cw > 0 && pw > 0 && cw != pw {
		x = (x * pw) / cw
	}
	if ch > 0 && ph > 0 && ch != ph {
		y = (y * ph) / ch
	}
	return x, y
}

func (a *Agent) handleCommand(data []byte) {
	var env struct {
		Type    string          `json:"type"`
		Payload json.RawMessage `json:"payload"`
	}

	if err := json.Unmarshal(data, &env); err != nil {
		return
	}

	switch env.Type {
	case "frame_ack":
		atomic.StoreInt32(&a.waitingAck, 0)

	case "consent_request":
		var p struct {
			AdminName string `json:"admin_name"`
			AdminRole string `json:"admin_role"`
		}
		if err := json.Unmarshal(env.Payload, &p); err != nil || p.AdminName == "" {
			p.AdminName = "Admin IT"
		}
		if p.AdminRole == "" {
			p.AdminRole = "Administrator"
		}

		if a.requireConsent() {
			log.Printf("[Agent] Consent request from [%s] (%s). Spawning prompt...", p.AdminName, p.AdminRole)
			a.consentApproved.Store(false)
			a.streaming.Store(false)
			_ = a.startRemoteSessionUI(p.AdminName, p.AdminRole)
		} else {
			log.Printf("[Agent] Unattended mode: Auto-approving session...")
			a.consentApproved.Store(true)
			a.sendJSON(map[string]interface{}{
				"type":       "consent_response",
				"approved":   true,
				"unattended": true,
			})
			a.streaming.Store(true)
			screen.PreventDisplaySleep(true)
			if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
				a.connMu.Lock()
				if a.conn != nil {
					atomic.StoreInt32(&a.waitingAck, 1)
					a.lastSentTime = time.Now()
					_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
				}
				a.connMu.Unlock()
			}
		}

	case "chat_message":
		// Viewer -> Client: Write line to ProjectRManager stdin
		a.sessionMu.Lock()
		if a.sessionStdin != nil {
			var p struct {
				Sender     string `json:"sender"`
				SenderName string `json:"sender_name"`
				Text       string `json:"text"`
				Timestamp  string `json:"timestamp"`
			}
			if err := json.Unmarshal(env.Payload, &p); err == nil {
				raw, _ := json.Marshal(map[string]interface{}{
					"type":        "chat_message",
					"sender":      p.Sender,
					"sender_name": p.SenderName,
					"text":        p.Text,
					"timestamp":   p.Timestamp,
				})
				_, _ = a.sessionStdin.Write(append(raw, '\n'))
			}
		}
		a.sessionMu.Unlock()

	case "end_session":
		log.Println("[Agent] Remote session ended by viewer.")
		a.streaming.Store(false)
		screen.PreventDisplaySleep(false)
		a.consentApproved.Store(false)
		a.stopSessionUI()

	case "mouse_move":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			X int `json:"x"`
			Y int `json:"y"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			mx, my := a.mapCoords(p.X, p.Y)
			input.MoveMouse(mx, my)
		}

	case "mouse_down":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			Button int `json:"button"`
			X      int `json:"x"`
			Y      int `json:"y"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			mx, my := a.mapCoords(p.X, p.Y)
			input.MouseDown(p.Button, mx, my)
		}

	case "mouse_up":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			Button int `json:"button"`
			X      int `json:"x"`
			Y      int `json:"y"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			mx, my := a.mapCoords(p.X, p.Y)
			input.MouseUp(p.Button, mx, my)
		}

	case "wheel":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			DeltaX int `json:"delta_x"`
			DeltaY int `json:"delta_y"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			input.MouseWheel(p.DeltaX, p.DeltaY)
		}

	case "key_down":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			Key  string `json:"key"`
			Code string `json:"code"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			input.KeyDown(p.Key, p.Code)
		}

	case "key_up":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			Key  string `json:"key"`
			Code string `json:"code"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			input.KeyUp(p.Key, p.Code)
		}

	case "clipboard":
		if a.requireConsent() && !a.consentApproved.Load() {
			return
		}
		var p struct {
			Text string `json:"text"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			input.SetClipboardText(p.Text)
		}

	case "quality":
		var p struct {
			FPS     int    `json:"fps"`
			Quality int    `json:"quality"`
			Mode    string `json:"mode"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			if p.FPS > 0 {
				atomic.StoreInt32(&a.fps, int32(p.FPS))
			}
			if p.Quality > 0 {
				a.capturer.SetQuality(p.Quality)
			}
			if p.Mode != "" {
				a.capturer.SetScaleMode(p.Mode)
			}
			// Send fresh keyframe with new resolution/quality settings
			if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
				a.connMu.Lock()
				if a.conn != nil {
					_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
				}
				a.connMu.Unlock()
			}
		}

	case "request_keyframe", "refresh":
		log.Println("[Agent] Keyframe requested. Sending full frame...")
		if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
			a.connMu.Lock()
			if a.conn != nil {
				atomic.StoreInt32(&a.waitingAck, 1)
				a.lastSentTime = time.Now()
				_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
			}
			a.connMu.Unlock()
		}

	case "status":
		var p struct {
			Code string `json:"code"`
		}
		if err := json.Unmarshal(env.Payload, &p); err == nil {
			if p.Code == "no_viewers" {
				log.Println("[Agent] All viewers left. Pausing screen capture.")
				a.streaming.Store(false)
				screen.PreventDisplaySleep(false)
				a.consentApproved.Store(false)
				a.stopSessionUI()
			} else if p.Code == "viewer_joined" {
				log.Println("[Agent] Viewer connected.")
				if !a.requireConsent() {
					log.Println("[Agent] Unattended mode. Resuming streaming & sending keyframe...")
					a.streaming.Store(true)
					screen.PreventDisplaySleep(true)
					if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
						a.connMu.Lock()
						if a.conn != nil {
							atomic.StoreInt32(&a.waitingAck, 1)
							a.lastSentTime = time.Now()
							_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
						}
						a.connMu.Unlock()
					}
				} else if a.consentApproved.Load() {
					log.Println("[Agent] Consent already approved in current session. Resuming streaming...")
					a.streaming.Store(true)
					screen.PreventDisplaySleep(true)
					if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
						a.connMu.Lock()
						if a.conn != nil {
							atomic.StoreInt32(&a.waitingAck, 1)
							a.lastSentTime = time.Now()
							_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
						}
						a.connMu.Unlock()
					}
				} else {
					log.Println("[Agent] Viewer connected and consent required. Prompting client user for approval...")
					a.streaming.Store(false)
					_ = a.startRemoteSessionUI("Admin IT", "Administrator")
				}
			}
		}
	}
}

// sendJSON writes a text JSON message to the connected websocket peer.
func (a *Agent) sendJSON(v interface{}) {
	data, err := json.Marshal(v)
	if err != nil {
		return
	}
	a.connMu.Lock()
	defer a.connMu.Unlock()
	if a.conn != nil {
		_ = a.conn.WriteMessage(websocket.TextMessage, data)
	}
}

// requireConsent checks policies.json to determine if user approval is required.
func (a *Agent) requireConsent() bool {
	commonAppData := os.Getenv("ProgramData")
	if commonAppData == "" {
		commonAppData = `C:\ProgramData`
	}
	policyPath := filepath.Join(commonAppData, "ProjectR", "Agent", "policies.json")
	if data, err := os.ReadFile(policyPath); err == nil {
		var p map[string]interface{}
		if err := json.Unmarshal(data, &p); err == nil {
			if req, ok := p["remote_require_consent"].(bool); ok {
				return req
			}
		}
	}
	return true // Default: enterprise policy requires consent
}

// startRemoteSessionUI launches ProjectRManager.exe in --remote-session mode with redirected stdio.
func (a *Agent) startRemoteSessionUI(adminName, adminRole string) error {
	a.sessionMu.Lock()
	defer a.sessionMu.Unlock()

	// Terminate any existing child process so fresh consent UI can appear
	if a.sessionStdin != nil {
		_ = a.sessionStdin.Close()
		a.sessionStdin = nil
	}
	if a.sessionCmd != nil && a.sessionCmd.Process != nil {
		_ = a.sessionCmd.Process.Kill()
		a.sessionCmd = nil
	}

	exePath, err := os.Executable()
	managerExe := ""
	if err == nil {
		managerExe = filepath.Join(filepath.Dir(exePath), "ProjectRManager.exe")
	}
	if managerExe == "" || !fileExists(managerExe) {
		managerExe = `C:\Program Files\Project R\Agent\ProjectRManager.exe`
	}
	if !fileExists(managerExe) {
		managerExe = `C:\ProgramData\ProjectR\Agent\ProjectRManager.exe`
	}

	log.Printf("[Agent] Launching Remote Session UI: %s --admin %q --role %q", managerExe, adminName, adminRole)
	cmd := exec.Command(managerExe, "--remote-session", "--admin", adminName, "--role", adminRole, "--timeout", "120")
	cmd.Dir = filepath.Dir(managerExe)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow: false,
	}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		_ = stdin.Close()
		return err
	}

	if err := cmd.Start(); err != nil {
		_ = stdin.Close()
		_ = stdout.Close()
		log.Printf("[Agent] Failed to start Remote Session UI: %v", err)
		return err
	}

	a.sessionCmd = cmd
	a.sessionStdin = stdin

	go a.readSessionStdout(stdout)

	go func() {
		_ = cmd.Wait()
		a.sessionMu.Lock()
		a.sessionCmd = nil
		a.sessionStdin = nil
		a.sessionMu.Unlock()
	}()

	return nil
}

// readSessionStdout reads JSON lines from the ProjectRManager child process.
func (a *Agent) readSessionStdout(r io.Reader) {
	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		log.Printf("[Agent] Session UI event: %s", line)

		var msg map[string]interface{}
		if err := json.Unmarshal([]byte(line), &msg); err != nil {
			continue
		}

		msgType, _ := msg["type"].(string)
		switch msgType {
		case "consent_result":
			approved, _ := msg["approved"].(bool)
			reason, _ := msg["reason"].(string)
			a.consentApproved.Store(approved)

			// Forward consent_response to relay -> viewer
			a.sendJSON(map[string]interface{}{
				"type":     "consent_response",
				"approved": approved,
				"reason":   reason,
			})

			if approved {
				log.Println("[Agent] Remote session APPROVED by user. Resuming screen capture...")
				a.streaming.Store(true)
				screen.PreventDisplaySleep(true)
				if packet, err := a.capturer.CaptureFrame(true); err == nil && packet != nil {
					a.connMu.Lock()
					if a.conn != nil {
						atomic.StoreInt32(&a.waitingAck, 1)
						a.lastSentTime = time.Now()
						_ = a.conn.WriteMessage(websocket.BinaryMessage, packet)
					}
					a.connMu.Unlock()
				}
			} else {
				log.Println("[Agent] Remote session REJECTED by user.")
				a.streaming.Store(false)
			}

		case "chat_message":
			// Forward client message to relay -> viewer
			a.sendJSON(msg)

		case "end_session":
			log.Println("[Agent] Client ended remote session.")
			a.streaming.Store(false)
			screen.PreventDisplaySleep(false)
			a.consentApproved.Store(false)
			a.sendJSON(map[string]interface{}{
				"type":   "session_ended",
				"reason": "Sesi diakhiri oleh pengguna di komputer klien.",
			})
		}
	}
}

// stopSessionUI cleanly shuts down the child session window.
func (a *Agent) stopSessionUI() {
	a.sessionMu.Lock()
	defer a.sessionMu.Unlock()

	if a.sessionStdin != nil {
		_, _ = a.sessionStdin.Write([]byte("{\"type\":\"session_ended\"}\n"))
		_ = a.sessionStdin.Close()
		a.sessionStdin = nil
	}
	if a.sessionCmd != nil && a.sessionCmd.Process != nil {
		_ = a.sessionCmd.Process.Kill()
		a.sessionCmd = nil
	}
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}
