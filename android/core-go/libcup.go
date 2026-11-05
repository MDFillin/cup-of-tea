// Package libcup — xray внутри приложения Android: запуск по JSON-конфигу, TUN от VpnService, счётчики трафика.
// Собирается в AAR через gomobile (android\scripts\build-core.ps1); типы в API — только те, что понимает gomobile.
package libcup

import (
	"bytes"
	"os"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"

	"github.com/xtls/xray-core/common/platform"
	"github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/features/stats"
	"github.com/xtls/xray-core/infra/conf/serial"
	_ "github.com/xtls/xray-core/main/distro/all"
)

// envLock: путь к базам и fd TUN передаются ядру через переменные окружения процесса,
// поэтому экземпляры создаются по одному.
var envLock sync.Mutex

// Version — версия xray внутри библиотеки.
func Version() string {
	return core.Version()
}

// SetMemoryLimit ограничивает кучу Go (мягко: сборщик мусора старается уложиться). mb <= 0 — без предела.
func SetMemoryLimit(mb int) {
	if mb <= 0 {
		debug.SetMemoryLimit(-1)
		return
	}
	debug.SetMemoryLimit(int64(mb) << 20)
}

// FreeMemory отдаёт системе память, которую куча Go больше не использует.
func FreeMemory() {
	debug.FreeOSMemory()
}

func newInstance(configJSON string) (*core.Instance, error) {
	c, err := serial.LoadJSONConfig(bytes.NewReader([]byte(configJSON)))
	if err != nil {
		return nil, err
	}
	return core.New(c)
}

// CheckConfig разбирает и собирает конфиг, ничего не запуская. assetDir — папка с geoip.dat и geosite.dat.
func CheckConfig(configJSON string, assetDir string) error {
	envLock.Lock()
	defer envLock.Unlock()
	setAssets(assetDir)
	_ = os.Unsetenv(envName(platform.TunFdKey))
	inst, err := newInstance(configJSON)
	if err != nil {
		return err
	}
	return inst.Close()
}

// Instance — работающий xray.
type Instance struct {
	mu    sync.Mutex
	inst  *core.Instance
	stats stats.Manager
}

// Start запускает xray. tunFd — дескриптор TUN от VpnService (вход tun в конфиге), -1 — без TUN
// (например, для проверки задержки). Дескриптор остаётся у приложения: xray работает со своей копией.
func Start(configJSON string, assetDir string, tunFd int) (*Instance, error) {
	envLock.Lock()
	defer envLock.Unlock()
	setAssets(assetDir)
	if tunFd >= 0 {
		_ = os.Setenv(envName(platform.TunFdKey), strconv.Itoa(tunFd))
	} else {
		_ = os.Unsetenv(envName(platform.TunFdKey))
	}
	inst, err := newInstance(configJSON)
	if err != nil {
		return nil, err
	}
	if err := inst.Start(); err != nil {
		_ = inst.Close()
		return nil, err
	}
	i := &Instance{inst: inst}
	if m, ok := inst.GetFeature(stats.ManagerType()).(stats.Manager); ok {
		i.stats = m
	}
	return i, nil
}

// Close останавливает xray; повторный вызов ничего не делает.
func (i *Instance) Close() error {
	i.mu.Lock()
	defer i.mu.Unlock()
	if i.inst == nil {
		return nil
	}
	err := i.inst.Close()
	i.inst = nil
	i.stats = nil
	return err
}

// Running — экземпляр запущен и не остановлен.
func (i *Instance) Running() bool {
	i.mu.Lock()
	defer i.mu.Unlock()
	return i.inst != nil && i.inst.IsRunning()
}

// Counter — значение счётчика статистики xray (например, "outbound>>>proxy>>>traffic>>>uplink");
// reset — обнулить после чтения. Нет статистики или такого счётчика — -1.
func (i *Instance) Counter(name string, reset bool) int64 {
	i.mu.Lock()
	defer i.mu.Unlock()
	if i.stats == nil {
		return -1
	}
	c := i.stats.GetCounter(name)
	if c == nil {
		return -1
	}
	if reset {
		return c.Set(0)
	}
	return c.Value()
}

// Traffic — сумма счётчиков всех выходов (весь трафик через ядро) в направлении "uplink" или "downlink";
// reset — обнулить после чтения. Вход tun своих счётчиков в xray не заводит, поэтому считаем по выходам,
// а у серверов из подписки теги выходов свои. Нет статистики — -1.
func (i *Instance) Traffic(direction string, reset bool) int64 {
	i.mu.Lock()
	defer i.mu.Unlock()
	if i.stats == nil {
		return -1
	}
	suffix := ">>>traffic>>>" + direction
	var total int64
	i.stats.VisitCounters(func(name string, c stats.Counter) bool {
		if strings.HasPrefix(name, "outbound>>>") && strings.HasSuffix(name, suffix) {
			if reset {
				total += c.Set(0)
			} else {
				total += c.Value()
			}
		}
		return true
	})
	return total
}

// CounterNames — имена всех счётчиков статистики через перевод строки (для диагностики).
func (i *Instance) CounterNames() string {
	i.mu.Lock()
	defer i.mu.Unlock()
	if i.stats == nil {
		return "(статистики нет)"
	}
	var b strings.Builder
	i.stats.VisitCounters(func(name string, c stats.Counter) bool {
		b.WriteString(name)
		b.WriteString("=")
		b.WriteString(strconv.FormatInt(c.Value(), 10))
		b.WriteString("\n")
		return true
	})
	return b.String()
}

func setAssets(assetDir string) {
	if assetDir != "" {
		_ = os.Setenv(envName(platform.AssetLocation), assetDir)
	}
}

// envName — имя переменной так, как его читает platform.NewEnvFlag: "xray.tun.fd" → "XRAY_TUN_FD".
func envName(key string) string {
	b := []byte(key)
	for n, ch := range b {
		switch {
		case ch == '.':
			b[n] = '_'
		case ch >= 'a' && ch <= 'z':
			b[n] = ch - 'a' + 'A'
		}
	}
	return string(b)
}
