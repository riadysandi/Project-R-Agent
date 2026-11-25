package input

import (
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

var (
	modUser32   = syscall.NewLazyDLL("user32.dll")
	modKernel32 = syscall.NewLazyDLL("kernel32.dll")

	procSetCursorPos      = modUser32.NewProc("SetCursorPos")
	procMouseEvent        = modUser32.NewProc("mouse_event")
	procKeybdEvent        = modUser32.NewProc("keybd_event")
	procOpenClipboard     = modUser32.NewProc("OpenClipboard")
	procCloseClipboard    = modUser32.NewProc("CloseClipboard")
	procEmptyClipboard    = modUser32.NewProc("EmptyClipboard")
	procSetClipboardData  = modUser32.NewProc("SetClipboardData")

	procGlobalAlloc       = modKernel32.NewProc("GlobalAlloc")
	procGlobalLock        = modKernel32.NewProc("GlobalLock")
	procGlobalUnlock      = modKernel32.NewProc("GlobalUnlock")
)

const (
	MOUSEEVENTF_MOVE       = 0x0001
	MOUSEEVENTF_LEFTDOWN   = 0x0002
	MOUSEEVENTF_LEFTUP     = 0x0004
	MOUSEEVENTF_RIGHTDOWN  = 0x0008
	MOUSEEVENTF_RIGHTUP    = 0x0010
	MOUSEEVENTF_MIDDLEDOWN = 0x0020
	MOUSEEVENTF_MIDDLEUP   = 0x0040
	MOUSEEVENTF_WHEEL      = 0x0800

	KEYEVENTF_EXTENDEDKEY = 0x0001
	KEYEVENTF_KEYUP       = 0x0002

	CF_UNICODETEXT = 13
	GMEM_MOVEABLE  = 0x0002
)

// Virtual Key Code Mapping
var vkMap = map[string]byte{
	"Backspace":  0x08,
	"Tab":        0x09,
	"Enter":      0x0D,
	"Shift":      0x10,
	"Control":    0x11,
	"Alt":        0x12,
	"Pause":      0x13,
	"CapsLock":   0x14,
	"Escape":     0x1B,
	"Space":      0x20,
	"PageUp":     0x21,
	"PageDown":   0x22,
	"End":        0x23,
	"Home":       0x24,
	"ArrowLeft":  0x25,
	"ArrowUp":    0x26,
	"ArrowRight": 0x27,
	"ArrowDown":  0x28,
	"PrintScreen":0x2C,
	"Insert":     0x2D,
	"Delete":     0x2E,
	"Meta":       0x5B, // Windows Key (Left Win)
	"ContextMenu":0x5D,
	"F1":         0x70,
	"F2":         0x71,
	"F3":         0x72,
	"F4":         0x73,
	"F5":         0x74,
	"F6":         0x75,
	"F7":         0x76,
	"F8":         0x77,
	"F9":         0x78,
	"F10":        0x79,
	"F11":        0x7A,
	"F12":        0x7B,
	"NumLock":    0x90,
	"ScrollLock": 0x91,
}

// MoveMouse moves the Windows cursor to absolute coordinates (x, y).
func MoveMouse(x, y int) {
	procSetCursorPos.Call(uintptr(x), uintptr(y))
}

// MouseDown sends a mouse button down event.
// button: 0 = Left, 1 = Middle, 2 = Right
func MouseDown(button, x, y int) {
	MoveMouse(x, y)
	var flag uintptr
	switch button {
	case 0:
		flag = MOUSEEVENTF_LEFTDOWN
	case 1:
		flag = MOUSEEVENTF_MIDDLEDOWN
	case 2:
		flag = MOUSEEVENTF_RIGHTDOWN
	default:
		flag = MOUSEEVENTF_LEFTDOWN
	}
	procMouseEvent.Call(flag, 0, 0, 0, 0)
}

// MouseUp sends a mouse button up event.
func MouseUp(button, x, y int) {
	MoveMouse(x, y)
	var flag uintptr
	switch button {
	case 0:
		flag = MOUSEEVENTF_LEFTUP
	case 1:
		flag = MOUSEEVENTF_MIDDLEUP
	case 2:
		flag = MOUSEEVENTF_RIGHTUP
	default:
		flag = MOUSEEVENTF_LEFTUP
	}
	procMouseEvent.Call(flag, 0, 0, 0, 0)
}

// MouseWheel sends a mouse scroll wheel event.
func MouseWheel(deltaX, deltaY int) {
	// Standard Windows wheel delta: 120 per notch. Invert deltaY for natural scroll.
	amount := -deltaY
	procMouseEvent.Call(uintptr(MOUSEEVENTF_WHEEL), 0, 0, uintptr(amount), 0)
}

// KeyDown injects a key down event.
func KeyDown(key, code string) {
	vk := resolveVK(key, code)
	if vk > 0 {
		var flags uintptr
		if isExtendedKey(vk) {
			flags |= KEYEVENTF_EXTENDEDKEY
		}
		procKeybdEvent.Call(uintptr(vk), 0, flags, 0)
	}
}

// KeyUp injects a key up event.
func KeyUp(key, code string) {
	vk := resolveVK(key, code)
	if vk > 0 {
		flags := uintptr(KEYEVENTF_KEYUP)
		if isExtendedKey(vk) {
			flags |= KEYEVENTF_EXTENDEDKEY
		}
		procKeybdEvent.Call(uintptr(vk), 0, flags, 0)
	}
}

func isExtendedKey(vk byte) bool {
	switch vk {
	case 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D:
		return true
	default:
		return false
	}
}

func resolveVK(key, code string) byte {
	// Check map first
	if vk, ok := vkMap[key]; ok {
		return vk
	}
	if vk, ok := vkMap[code]; ok {
		return vk
	}

	// Single character key
	if len(key) == 1 {
		ch := key[0]
		// A-Z
		if ch >= 'a' && ch <= 'z' {
			return ch - 'a' + 0x41
		}
		if ch >= 'A' && ch <= 'Z' {
			return ch
		}
		// 0-9
		if ch >= '0' && ch <= '9' {
			return ch
		}
		// Common punctuation
		switch ch {
		case ' ':
			return 0x20
		case ';', ':':
			return 0xBA
		case '=', '+':
			return 0xBB
		case ',', '<':
			return 0xBC
		case '-', '_':
			return 0xBD
		case '.', '>':
			return 0xBE
		case '/', '?':
			return 0xBF
		case '`', '~':
			return 0xC0
		case '[', '{':
			return 0xDB
		case '\\', '|':
			return 0xDC
		case ']', '}':
			return 0xDD
		case '\'', '"':
			return 0xDE
		}
	}

	// Check KeyA-KeyZ codes
	if strings.HasPrefix(code, "Key") && len(code) == 4 {
		return code[3]
	}
	// Check Digit0-Digit9 codes
	if strings.HasPrefix(code, "Digit") && len(code) == 6 {
		return code[5]
	}

	return 0
}

// SetClipboardText places UTF-16 text into the Windows clipboard.
func SetClipboardText(text string) bool {
	if text == "" {
		return false
	}

	ret, _, _ := procOpenClipboard.Call(0)
	if ret == 0 {
		return false
	}
	defer procCloseClipboard.Call()

	procEmptyClipboard.Call()

	utf16Chars := utf16.Encode([]rune(text + "\x00"))
	sizeBytes := uintptr(len(utf16Chars) * 2)

	hMem, _, _ := procGlobalAlloc.Call(uintptr(GMEM_MOVEABLE), sizeBytes)
	if hMem == 0 {
		return false
	}

	ptr, _, _ := procGlobalLock.Call(hMem)
	if ptr == 0 {
		return false
	}

	dest := unsafe.Slice((*uint16)(unsafe.Pointer(ptr)), len(utf16Chars))
	copy(dest, utf16Chars)
	procGlobalUnlock.Call(hMem)

	setRet, _, _ := procSetClipboardData.Call(uintptr(CF_UNICODETEXT), hMem)
	return setRet != 0
}
