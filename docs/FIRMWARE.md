# Firmware research and preparation

**Current post-reboot validation:** the installed 708-byte EP2-guard OL4 passed
normal-input checks after one short command, a full initialized frame, opening
the HUB, a timed ten-second live stream and stopping it. The owner confirmed
simultaneous input and updating widgets; passive G9 events independently confirm
movement and left clicks during live output. The assigned key also worked after
stop. No new flash followed the Windows reboot that recovered the earlier failed
session; its persistent cause is not fully isolated. The positioned 84×20 PNG at
(18,4) was also visibly confirmed with cursor/clicks working. The final receiver
test passed for movement, left/right clicks and the assigned key. The owner saw
the stock screen after changing from cable to radio, so cross-mode image
retention is unsupported; the exact reset trigger is not proven. Source
publication/CI remain unfinished.

Updated: 2026-11-16. **Owner-authorized experiments have installed NoWait,
OL3 and OL4 on the development mouse.** Ordinary OLED-button key
output was confirmed over USB and 2.4 GHz without the HUB. The owner also
confirmed the initial OL4's first visible control image. An earlier OL4 with
the discovery-race correction completed 33/33 blocks; four consecutive
physical status/version cycles passed with valid GS4 CRCs and correct following
version headers/markers. The owner confirmed that live widgets are visible and
updating while the HUB runs. Sleep/retention and independent firmware recovery
remain unverified. Supported original settings were restored with strict
readback; this is not a bit-exact profile or full-firmware restoration. These
earlier visible-widget results are distinct from the current post-reboot
EP2-guard retest above. The guard has 53 offline FIFO/readiness cases; its one
four-byte USB callback hook affects report 22 only.

The owner explicitly accepted permanent device loss and replacement, superseding
the earlier owner-specific requirement to prove recovery before installation.
Independent recovery is still unverified, so this is not a general safe-install
claim. See [current recovery evidence](RECOVERY.md) and the
[OL4 USB investigation](OLED-USB-CHUNKS.md). The builders described below only
prepare local candidates; they do not flash or enter the bootloader.

## Обработка назначений кнопки-экрана

Шестая кнопка G9 PRO — нажимаемая площадка с OLED. В исследованной исходной прошивке её
удержание управляет экраном. Когда она нажата отдельно, прошивка сначала
вооружает OLED-жест и откладывает обработку обычного нажатия. При отпускании
клавиатурная последовательность может отмениться до отправки самой клавиши.

До установки NoWait диагноз проверен сравнением одинакового назначения `11 00 27`:

| Физический тест | Результат |
|---|---|
| Обычная «0» на нажатии колёсика | Печатает «0» |
| Обычная «0» на кнопке-экране отдельно | Не печатает |
| Кнопка-экран при зажатой правой кнопке | Печатает «0» |

Таким образом, клавиатурный USB-тракт работает, а проблема связана с обработкой
экранной кнопки. Проверки включали настоящий ввод в текстовое поле и события
Raw Input от конкретной мыши. Наблюдение USB completion подтвердило передачу
клавиатурных отчётов, но **байты USB-пакетов через ETW на этой Windows получены
не были** — это не выдаётся за побайтовый захват.

Оригинальные инструкции обработчиков исполнены в Unicorn. Они воспроизводят
ошибку для отдельной экранной кнопки и нормальную работу колёсика/сочетания
с правой. ROM-вызовы и оборудование в этой модели заменены заглушками,
готовность endpoint предполагается; это не заменяет физическую проверку.

### Минимальный кандидат исправления

Для одного конкретного официального образа 1.08 подготовлена замена инструкции,
которая включает ожидание OLED-удержания:

```text
ARM Thumb VA:       0x3201C
Инструкция:        0D 70 -> C0 46
BIN resource:      0xA212
CRC16/CMS блока:   53 AA -> 34 33, смещение 0xA216
```

Изменяются два байта кода, два байта проверочного слова блока и четыре байта
контрольной суммы тела в заголовке. Размер образа и остальные данные
сохраняются. **Общая задержка кнопок не увеличивается.** Подробности и
отбраковка прежних кандидатов: [проверка заголовка](FIRMWARE-HEADER.md).

Долгое удержание площадки после такого исправления больше не должно
переключать OLED; обычные отрисовка, сон и включение экрана этим патчем не
заменяются. DPI-клик и клавиатурные сценарии прошли офлайн-проверки при включённом
и выключенном OLED. Позже NoWait установлен: отдельное нажатие кнопки-экрана
выдаёт обычную клавишу по USB и 2.4G при закрытом HUB. Эти проверки не доказывают
восстановление прошивки и не заменяют отдельные проверки сна/удержания картинки.
Живые виджеты позже подтверждены пользователем на исправленном OL4.

## Подготовка прошивки

Исходные программы и прошивки производителя **не включены в репозиторий**.
Скачивать их нужно с [официальной страницы G9 PRO](https://www.furycube.com/pages/g9-pro).
Поддерживаемый вход: архив `G9_PRO_BT_Ver1.08_Upgrade_20251028.rar`, официальный
`Update.exe` из него размещается в `evidence/firmware-readonly/<папка>/Update.exe`.

Ожидаемый SHA256 обновлятора:

```text
558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4
```

HUB кнопкой «Подготовить исправление»:

- проверяет исходные SHA256, инструкцию и CRC;
- создаёт отдельную копию с изменённым BIN-ресурсом;
- сверяет итоговый ресурс и неизменность остальных PE-секций;
- пишет `patch-report.json` и пояснение рядом с результатом;
- оставляет кандидату расширение `.exe.disabled` и **не запускает его**.

Отдельная Python-проверка дополнительно сверяет все **3939** блоков CRC16/CMS
и точные восемь байтов diff, включая CRC32 тела и длину заголовка. Она находится
в `tools/verify_firmware_candidate.py`.

**Почему нет кнопки автоматической установки:** штатный обновлятор не создаёт
копию установленной прошивки, недостаточно проверяет аппаратную ревизию и
стирает более широкую область, чем записывает. Совпадения PID и номера 1.08
недостаточно для гарантии совместимости. До проверки текущего образа и способа
восстановления проект не считает перепрошивку безопасным автоматическим шагом.

Подробности OLED и протокола: [OLED-PROTOCOL.md](../firmware/OLED-PROTOCOL.md).
Состояние этапов: [PLAN.md](../PLAN.md).
