package libcup

import (
	"context"
	"errors"
	"io"
	"net"
	"net/http"
	"strings"
	"time"

	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/session"
	"github.com/xtls/xray-core/core"
)

// Проверки идут через само ядро (core.Dial или выход по тегу), а не через локальный прокси-порт:
// открытый SOCKS/HTTP на 127.0.0.1 видят другие приложения — некоторые по нему узнают VPN.

// httpClient — клиент, чьи соединения открывает xray: через выход tag или, если tag пустой, по маршрутам конфига.
func (i *Instance) httpClient(tag string, timeout time.Duration) (*http.Client, error) {
	i.mu.Lock()
	inst := i.inst
	i.mu.Unlock()
	if inst == nil {
		return nil, errors.New("xray не запущен")
	}
	tr := &http.Transport{
		Proxy: nil,
		DialContext: func(ctx context.Context, network, addr string) (net.Conn, error) {
			dest, err := xnet.ParseDestination("tcp:" + addr)
			if err != nil {
				return nil, err
			}
			if tag != "" {
				// Как tagged/taggedimpl (наблюдатель xray): выход по тегу, адрес разрешает сам сервер.
				// core.Dial кладёт экземпляр в контекст сам, поэтому идём через него.
				ctx = session.ContextWithContent(ctx, &session.Content{SkipDNSResolve: true})
				ctx = session.SetForcedOutboundTagToContext(ctx, tag)
			}
			return core.Dial(ctx, inst, dest)
		},
		MaxIdleConns:          4,
		IdleConnTimeout:       60 * time.Second,
		TLSHandshakeTimeout:   timeout,
		ResponseHeaderTimeout: timeout,
		DisableCompression:    true,
	}
	return &http.Client{Transport: tr, Timeout: timeout}, nil
}

func fetch(client *http.Client, url string, limit int64) (string, int, error) {
	resp, err := client.Get(url)
	if err != nil {
		return "", 0, describe(err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, limit))
	if err != nil {
		return "", resp.StatusCode, describe(err)
	}
	return string(body), resp.StatusCode, nil
}

// Delay — «реальная задержка» через выход tag (пустой — по маршрутам конфига): первый запрос к url прогревает
// туннель и TLS, затем лучшее из samples замеров по тому же соединению. Миллисекунды.
func (i *Instance) Delay(tag string, url string, timeoutMs int, samples int) (int64, error) {
	client, err := i.httpClient(tag, time.Duration(timeoutMs)*time.Millisecond)
	if err != nil {
		return -1, err
	}
	defer client.CloseIdleConnections()
	if _, code, err := fetch(client, url, 4096); err != nil {
		return -1, err
	} else if code >= 400 {
		return -1, errors.New("сайт проверки ответил " + http.StatusText(code))
	}
	if samples < 1 {
		samples = 1
	}
	best := int64(-1)
	for n := 0; n < samples; n++ {
		start := time.Now()
		if _, _, err := fetch(client, url, 4096); err != nil {
			return -1, err
		}
		if ms := time.Since(start).Milliseconds(); best < 0 || ms < best {
			best = ms
		}
	}
	return best, nil
}

// Get — тело ответа (до 64 КБ) через выход tag или по маршрутам конфига; например, внешний IP.
func (i *Instance) Get(tag string, url string, timeoutMs int) (string, error) {
	client, err := i.httpClient(tag, time.Duration(timeoutMs)*time.Millisecond)
	if err != nil {
		return "", err
	}
	defer client.CloseIdleConnections()
	body, code, err := fetch(client, url, 64*1024)
	if err != nil {
		return "", err
	}
	if code >= 400 {
		return "", errors.New("сайт ответил " + http.StatusText(code))
	}
	return strings.TrimSpace(body), nil
}

// Download — скачивает до limitBytes с url по маршрутам конфига и возвращает скорость, байт/с (замер скорости).
func (i *Instance) Download(url string, limitBytes int64, timeoutMs int) (int64, error) {
	client, err := i.httpClient("", time.Duration(timeoutMs)*time.Millisecond)
	if err != nil {
		return -1, err
	}
	defer client.CloseIdleConnections()
	start := time.Now()
	resp, err := client.Get(url)
	if err != nil {
		return -1, describe(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		return -1, errors.New("сайт ответил " + http.StatusText(resp.StatusCode))
	}
	n, err := io.Copy(io.Discard, io.LimitReader(resp.Body, limitBytes))
	elapsed := time.Since(start).Seconds()
	if n == 0 && err != nil {
		return -1, describe(err)
	}
	if elapsed <= 0 {
		elapsed = 0.001
	}
	return int64(float64(n) / elapsed), nil
}

// describe — короткая причина вместо длинной цепочки ошибок Go.
func describe(err error) error {
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return errors.New("нет ответа")
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return errors.New("нет ответа")
	}
	return err
}
