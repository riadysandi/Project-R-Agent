package main

import (
	"encoding/json"
	"flag"
	"log"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"runtime/debug"
	"strings"
	"syscall"
	"unsafe"

	"github.com/project-r/windows-remote-agent/client"
	"github.com/project-r/windows-remote-agent/screen"
	"golang.org/x/sys/windows/registry"
)

var (
	modKernel32     = syscall.NewLazyDLL("kernel32.dll")
	procCreateMutex = modKernel32.NewProc("CreateMutexW")
)

var (
	version = "2.4.7"

	serverFlag   = flag.String("server", "", "Project R Relay / Server URL")
	deviceIDFlag = flag.String("device-id", "", "Device ID (defaults to hardware UUID or registry)")
	tokenFlag    = flag.String("token", "", "Agent authentication token")
	fpsFlag      = flag.Int("fps", 20, "Target screen capture frame rate (FPS)")
	qualityFlag  = flag.Int("quality", 65, "JPEG compression quality (1-100)")
)

// AgentConfigFile represents C:\ProgramData\ProjectR\Agent\agent.json
type AgentConfigFile struct {
	ServerUrl string `json:"ServerUrl"`
	Token     string `json:"Token"`
	Tag       string `json:"Tag"`
	Version   string `json:"Version"`
	DeviceId  string `json:"DeviceId"`
}

func loadExistingAgentConfig() *AgentConfigFile {
	commonAppData := os.Getenv("ProgramData")
	if commonAppData == "" {
		commonAppData = `C:\ProgramData`
	}
	configPath := filepath.Join(commonAppData, "ProjectR", "Agent", "agent.json")

	data, err := os.ReadFile(configPath)
	if err != nil {
		return nil
	}

	var cfg AgentConfigFile
	if err := json.Unmarshal(data, &cfg); err != nil {
		return nil
	}
	return &cfg
}

func resolveDeviceID() string {
	// 1. Try reading from ProjectR registry
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, `SOFTWARE\ProjectR`, registry.QUERY_VALUE)
	if err == nil {
		defer k.Close()
		if devID, _, err := k.GetStringValue("DeviceId"); err == nil && devID != "" {
			return devID
		}
	}

	// 2. Query SMBIOS UUID (matches SystemInfo.cs Win32_ComputerSystemProduct.UUID)
	cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", "(Get-CimInstance Win32_ComputerSystemProduct).UUID")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, err := cmd.Output()
	if err == nil {
		uuid := strings.TrimSpace(string(out))
		if uuid != "" && !strings.HasPrefix(uuid, "00000000") && !strings.HasPrefix(uuid, "FFFFFFFF") {
			return uuid
		}
	}

	// 3. Try reading Windows MachineGuid
	k2, err := registry.OpenKey(registry.LOCAL_MACHINE, `SOFTWARE\Microsoft\Cryptography`, registry.QUERY_VALUE)
	if err == nil {
		defer k2.Close()
		if guid, _, err := k2.GetStringValue("MachineGuid"); err == nil && guid != "" {
			return guid
		}
	}

	// 4. Fallback to MachineName
	hostname, err := os.Hostname()
	if err == nil && hostname != "" {
		return hostname
	}

	return "PR-WIN-CLIENT"
}

func main() {
	flag.Parse()
	screen.EnableDPIAwareness()

	commonAppData := os.Getenv("ProgramData")
	if commonAppData == "" {
		commonAppData = `C:\ProgramData`
	}
	logDir := filepath.Join(commonAppData, "ProjectR", "Agent")
	_ = os.MkdirAll(logDir, 0755)
	logFile, err := os.OpenFile(filepath.Join(logDir, "remote.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
	if err == nil {
		defer logFile.Close()
		log.SetOutput(logFile)
	}

	defer func() {
		if r := recover(); r != nil {
			log.Printf("[FATAL PANIC] %v\nStack:\n%s", r, debug.Stack())
		}
	}()

	// Single instance mutex per active desktop session
	mutexName, _ := syscall.UTF16PtrFromString("Local\\ProjectR_RemoteAgent_SingleInstance_Mutex")
	hMutex, _, errMutex := procCreateMutex.Call(0, 1, uintptr(unsafe.Pointer(mutexName)))
	if hMutex == 0 || (errMutex != nil && errMutex.(syscall.Errno) == syscall.ERROR_ALREADY_EXISTS) {
		log.Printf("[Main] Mutex ERROR_ALREADY_EXISTS: Another ProjectRRemoteAgent instance is already running (hMutex: %v). Exiting cleanly.", hMutex)
		return
	}
	defer syscall.CloseHandle(syscall.Handle(hMutex))

	serverURL := *serverFlag
	token := *tokenFlag
	deviceID := *deviceIDFlag

	// Auto-discover configuration from existing Project R Agent if not provided
	localCfg := loadExistingAgentConfig()
	if localCfg != nil {
		if serverURL == "" {
			serverURL = localCfg.ServerUrl
		}
		if token == "" {
			token = localCfg.Token
		}
		if deviceID == "" && localCfg.DeviceId != "" {
			deviceID = localCfg.DeviceId
		}
	}

	if serverURL == "" {
		log.Fatal("[Main] Server URL is required. Pass --server or configure agent.json.")
	}
	if deviceID == "" {
		deviceID = resolveDeviceID()
	}

	log.Printf("=============================================================")
	log.Printf("  Project R Windows Remote Desktop Agent v%s (Golang)", version)
	log.Printf("  Server:    %s", serverURL)
	log.Printf("  Device ID: %s", deviceID)
	log.Printf("  FPS:       %d | Quality: %d", *fpsFlag, *qualityFlag)
	log.Printf("=============================================================")

	agentCfg := client.Config{
		ServerURL: serverURL,
		DeviceID:  deviceID,
		Token:     token,
		FPS:       *fpsFlag,
		Quality:   *qualityFlag,
	}

	log.Printf("[Main] Initializing client.NewAgent...")
	agent := client.NewAgent(agentCfg)
	log.Printf("[Main] client.NewAgent initialized successfully.")

	// Graceful shutdown handling
	stopChan := make(chan os.Signal, 1)
	signal.Notify(stopChan, os.Interrupt, syscall.SIGTERM)

	go func() {
		sig := <-stopChan
		log.Printf("[Main] Received signal %v. Terminating Windows Remote Agent...", sig)
		agent.Stop()
		os.Exit(0)
	}()

	log.Printf("[Main] Calling agent.Start()...")
	agent.Start()
	log.Printf("[Main] agent.Start() returned! Process exiting.")
}
