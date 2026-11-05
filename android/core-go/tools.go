//go:build tools

// gomobile bind ищет пакет bind в зависимостях модуля — держим его здесь.
package libcup

import _ "golang.org/x/mobile/bind"
