package screen

import (
	"bytes"
	"encoding/binary"
	"image"
	"image/jpeg"
	"log"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

var (
	modUser32   = syscall.NewLazyDLL("user32.dll")
	modGdi32    = syscall.NewLazyDLL("gdi32.dll")
	modKernel32 = syscall.NewLazyDLL("kernel32.dll")

	procSetProcessDpiAwarenessContext = modUser32.NewProc("SetProcessDpiAwarenessContext")
	procSetProcessDPIAware            = modUser32.NewProc("SetProcessDPIAware")
	procOpenWindowStation             = modUser32.NewProc("OpenWindowStationW")
	procSetProcessWindowStation       = modUser32.NewProc("SetProcessWindowStation")
	procOpenDesktop                   = modUser32.NewProc("OpenDesktopW")
	procOpenInputDesktop              = modUser32.NewProc("OpenInputDesktop")
	procSetThreadDesktop              = modUser32.NewProc("SetThreadDesktop")
	procGetDC                         = modUser32.NewProc("GetDC")
	procReleaseDC                     = modUser32.NewProc("ReleaseDC")
	procGetSystemMetrics              = modUser32.NewProc("GetSystemMetrics")
	procCreateCompatibleDC            = modGdi32.NewProc("CreateCompatibleDC")
	procCreateCompatibleBmp           = modGdi32.NewProc("CreateCompatibleBitmap")
	procSelectObject                  = modGdi32.NewProc("SelectObject")
	procBitBlt                        = modGdi32.NewProc("BitBlt")
	procStretchBlt                    = modGdi32.NewProc("StretchBlt")
	procSetStretchBltMode             = modGdi32.NewProc("SetStretchBltMode")
	procGetDIBits                     = modGdi32.NewProc("GetDIBits")
	procDeleteDC                      = modGdi32.NewProc("DeleteDC")
	procDeleteObject                  = modGdi32.NewProc("DeleteObject")
	procSetThreadExecutionState       = modKernel32.NewProc("SetThreadExecutionState")
)

const (
	SM_CXSCREEN    = 0
	SM_CYSCREEN    = 1
	SRCCOPY        = 0x00CC0020
	DIB_RGB_COLORS = 0
	BI_RGB         = 0
	GENERIC_ALL    = 0x10000000

	COLORONCOLOR                               = 3

	ES_SYSTEM_REQUIRED  = 0x00000001
	ES_DISPLAY_REQUIRED = 0x00000002
	ES_CONTINUOUS       = 0x80000000
)

// dpiAwarenessContextPerMonitorAwareV2 represents Win32 ((DPI_AWARENESS_CONTEXT)-4)
var dpiAwarenessContextPerMonitorAwareV2 = ^uintptr(3)

// EnableDPIAwareness ensures Windows reports true physical monitor dimensions
// rather than 1280x800 scaled virtual metrics on high-DPI displays (125%, 150%, 200%).
func EnableDPIAwareness() {
	if procSetProcessDpiAwarenessContext.Find() == nil {
		procSetProcessDpiAwarenessContext.Call(dpiAwarenessContextPerMonitorAwareV2)
	} else if procSetProcessDPIAware.Find() == nil {
		procSetProcessDPIAware.Call()
	}
}

// PreventDisplaySleep keeps display and system awake during remote streaming
func PreventDisplaySleep(enable bool) {
	if enable {
		procSetThreadExecutionState.Call(uintptr(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED))
	} else {
		procSetThreadExecutionState.Call(uintptr(ES_CONTINUOUS))
	}
}

// AttachToDesktop is a safe no-op. The agent process is already attached to winsta0\default by SessionLauncher.
func AttachToDesktop() {
}

type BITMAPINFOHEADER struct {
	BiSize          uint32
	BiWidth         int32
	BiHeight        int32
	BiPlanes        uint16
	BiBitCount      uint16
	BiCompression   uint32
	BiSizeImage     uint32
	BiXPelsPerMeter int32
	BiYPelsPerMeter int32
	BiClrUsed       uint32
	BiClrImportant  uint32
}

type BITMAPINFO struct {
	BmiHeader BITMAPINFOHEADER
	BmiColors [1]uint32
}

// Capturer handles screen capture and frame encoding.
type Capturer struct {
	PhysicalWidth  int
	PhysicalHeight int
	CaptureWidth   int
	CaptureHeight  int
	Quality        int
	ScaleMode      string // "auto", "high", "low", "eco"
	seq            uint32
	lastHash       uint64
	unchangedCt    int
	bufPool        sync.Pool
	mu             sync.Mutex
}

// NewCapturer initializes screen capturer with DPI awareness and primary display dimensions.
func NewCapturer(quality int) *Capturer {
	if quality <= 0 || quality > 100 {
		quality = 55
	}

	log.Println("[Capturer] Calling GetScreenSize()...")
	pw, ph := GetScreenSize()
	log.Printf("[Capturer] NewCapturer: physical screen size is %dx%d", pw, ph)

	c := &Capturer{
		PhysicalWidth:  pw,
		PhysicalHeight: ph,
		Quality:        quality,
		ScaleMode:      "auto",
		bufPool: sync.Pool{
			New: func() interface{} {
				return new(bytes.Buffer)
			},
		},
	}
	log.Println("[Capturer] Calling recomputeDimensions()...")
	c.recomputeDimensions(pw, ph)
	log.Printf("[Capturer] NewCapturer: initialized with CaptureSize %dx%d", c.CaptureWidth, c.CaptureHeight)
	return c
}

// recomputeDimensions calculates target capture width/height based on physical screen and mode
func (c *Capturer) recomputeDimensions(pw, ph int) {
	c.PhysicalWidth = pw
	c.PhysicalHeight = ph

	if pw <= 0 || ph <= 0 {
		c.CaptureWidth = 1920
		c.CaptureHeight = 1080
		return
	}

	switch c.ScaleMode {
	case "high":
		// Native full resolution (e.g. 1920x1200)
		c.CaptureWidth = pw
		c.CaptureHeight = ph
	case "low", "eco":
		// Compact 960px width
		if pw > 960 {
			c.CaptureWidth = 960
			c.CaptureHeight = (ph * 960) / pw
		} else {
			c.CaptureWidth = pw
			c.CaptureHeight = ph
		}
	default: // "auto" / "balanced"
		// 1366px max width gives the sweet spot: crisp text and 25-30 FPS speed
		maxWidth := 1366
		if pw > maxWidth {
			c.CaptureWidth = maxWidth
			c.CaptureHeight = (ph * maxWidth) / pw
		} else {
			c.CaptureWidth = pw
			c.CaptureHeight = ph
		}
	}

	// Dimensions must be even for standard JPEG DCT encoding
	if c.CaptureWidth%2 != 0 {
		c.CaptureWidth--
	}
	if c.CaptureHeight%2 != 0 {
		c.CaptureHeight--
	}
}

// GetDimensions returns current capture and physical dimensions
func (c *Capturer) GetDimensions() (int, int, int, int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.CaptureWidth, c.CaptureHeight, c.PhysicalWidth, c.PhysicalHeight
}

// SetScaleMode dynamically sets scaling ("auto", "high", "low", "eco")
func (c *Capturer) SetScaleMode(mode string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.ScaleMode = mode
	c.recomputeDimensions(c.PhysicalWidth, c.PhysicalHeight)
}

// GetScreenSize returns current primary monitor resolution with DPI awareness.
func GetScreenSize() (int, int) {
	w, _, _ := procGetSystemMetrics.Call(uintptr(SM_CXSCREEN))
	h, _, _ := procGetSystemMetrics.Call(uintptr(SM_CYSCREEN))
	if w == 0 || h == 0 {
		return 1920, 1080
	}
	return int(w), int(h)
}

// CaptureFrame captures the desktop screen and returns the encoded binary packet.
// Binary format: [0x01][4 bytes seq][2 bytes format 0x01][JPEG bytes]
// Returns nil if frame has not changed (idle optimization) unless force is true.
func (c *Capturer) CaptureFrame(force bool) ([]byte, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	pw, ph := GetScreenSize()
	if pw != c.PhysicalWidth || ph != c.PhysicalHeight {
		c.recomputeDimensions(pw, ph)
	}

	cw := c.CaptureWidth
	ch := c.CaptureHeight

	hdcScreen, _, _ := procGetDC.Call(0)
	if hdcScreen == 0 {
		return nil, syscall.GetLastError()
	}
	defer procReleaseDC.Call(0, hdcScreen)

	hdcMem, _, _ := procCreateCompatibleDC.Call(hdcScreen)
	if hdcMem == 0 {
		return nil, syscall.GetLastError()
	}
	defer procDeleteDC.Call(hdcMem)

	hBitmap, _, _ := procCreateCompatibleBmp.Call(hdcScreen, uintptr(cw), uintptr(ch))
	if hBitmap == 0 {
		return nil, syscall.GetLastError()
	}
	defer procDeleteObject.Call(hBitmap)

	oldBmp, _, _ := procSelectObject.Call(hdcMem, hBitmap)

	// High speed hardware capture & scaling
	if cw == pw && ch == ph {
		procBitBlt.Call(hdcMem, 0, 0, uintptr(cw), uintptr(ch), hdcScreen, 0, 0, uintptr(SRCCOPY))
	} else {
		procSetStretchBltMode.Call(hdcMem, uintptr(COLORONCOLOR))
		procStretchBlt.Call(
			hdcMem, 0, 0, uintptr(cw), uintptr(ch),
			hdcScreen, 0, 0, uintptr(pw), uintptr(ph),
			uintptr(SRCCOPY),
		)
	}

	// CRITICAL: Unselect bitmap from hdcMem BEFORE GetDIBits!
	procSelectObject.Call(hdcMem, oldBmp)

	var bi BITMAPINFO
	bi.BmiHeader.BiSize = uint32(unsafe.Sizeof(bi.BmiHeader))
	bi.BmiHeader.BiWidth = int32(cw)
	bi.BmiHeader.BiHeight = -int32(ch) // Negative for top-down DIB
	bi.BmiHeader.BiPlanes = 1
	bi.BmiHeader.BiBitCount = 32
	bi.BmiHeader.BiCompression = BI_RGB

	rawBytes := make([]byte, cw*ch*4)
	ret, _, _ := procGetDIBits.Call(
		hdcScreen,
		hBitmap,
		0,
		uintptr(ch),
		uintptr(unsafe.Pointer(&rawBytes[0])),
		uintptr(unsafe.Pointer(&bi)),
		uintptr(DIB_RGB_COLORS),
	)

	if ret == 0 {
		return nil, syscall.GetLastError()
	}

	// Fast 64-bit sampling hash for diff detection (sample every 64 pixels)
	var hash uint64 = 14695981039346656037
	step := 64 * 4
	for i := 0; i < len(rawBytes); i += step {
		hash ^= uint64(rawBytes[i]) | (uint64(rawBytes[i+1]) << 8) | (uint64(rawBytes[i+2]) << 16)
		hash *= 1099511628211
	}

	if !force && hash == c.lastHash {
		c.unchangedCt++
		// If unchanged for less than 15 consecutive checks, skip sending to save bandwidth
		if c.unchangedCt < 15 {
			return nil, nil
		}
	}
	c.unchangedCt = 0
	c.lastHash = hash

	// Convert BGRA to RGBA image
	img := &image.RGBA{
		Pix:    make([]byte, len(rawBytes)),
		Stride: cw * 4,
		Rect:   image.Rect(0, 0, cw, ch),
	}

	for i := 0; i < len(rawBytes); i += 4 {
		// DIB gives B, G, R, A
		b := rawBytes[i]
		g := rawBytes[i+1]
		r := rawBytes[i+2]
		img.Pix[i] = r
		img.Pix[i+1] = g
		img.Pix[i+2] = b
		img.Pix[i+3] = 255
	}

	// Encode to JPEG
	buf := c.bufPool.Get().(*bytes.Buffer)
	buf.Reset()
	defer c.bufPool.Put(buf)

	// Write 7-byte binary header:
	// Byte 0: 0x01 (Binary Frame Magic)
	// Byte 1-4: Sequence / Timestamp (BigEndian)
	// Byte 5-6: Format (0x0001 = JPEG)
	c.seq++
	header := make([]byte, 7)
	header[0] = 0x01
	binary.BigEndian.PutUint32(header[1:5], c.seq)
	binary.BigEndian.PutUint16(header[5:7], 0x0001)

	buf.Write(header)

	opts := &jpeg.Options{Quality: c.Quality}
	if err := jpeg.Encode(buf, img, opts); err != nil {
		return nil, err
	}

	// Copy result bytes
	out := make([]byte, buf.Len())
	copy(out, buf.Bytes())
	return out, nil
}

// SetQuality dynamically adjusts JPEG compression quality.
func (c *Capturer) SetQuality(q int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if q >= 10 && q <= 100 {
		c.Quality = q
	}
}

// NowMillis returns current millisecond timestamp.
func NowMillis() int64 {
	return time.Now().UnixNano() / int64(time.Millisecond)
}
