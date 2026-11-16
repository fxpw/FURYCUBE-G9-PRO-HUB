# OEM receiver framing: API boundary audit

Updated 2026-11-16. The OEM HIDAPI path pads an eight-byte A0 prepare to the
receiver's 134-byte feature capacity. Our direct HidD transport previously
passed eight bytes. This is a verified API-buffer difference, but the controlled
DG2 experiment below shows that padding alone does not repair the current
radio configuration timeout.

## Reproducible offline evidence

Run `tools/pyenv/bin/python tools/audit_vendor_rf_framing.py` under WSL.
The script refuses unrecognized local OEM binary hashes, executes their x86
instructions in Unicorn, supplies every OS call, and writes only a unique JSON
under ignored `evidence/rf-host-framing/`. It never loads a DLL into Windows,
opens a device, compiles, runs an updater or changes cached artifacts. Vendor
binaries are not included in the source repository.

Validated output: `evidence/rf-host-framing/20261116T205234935739Z-7b03bafa.json`,
SHA256 `606ec8b917f888ff3b92ce725909b53ccc35e19bbd2c361606282249b795a967`.
The source SHA256 pins are stored in the script and output. The OEM HIDAPI pin
is `f4e23fe7182822b57305a8aee056a38e875c047e0785e598c1cfd2c1ab7a92bd`.

The fixture executed the actual OEM prepare helper `0x420300` with supplied
normal-radio model flags, the HIDAPI send/get implementations, direct
`hiddriver_1.dll.SetFeature`, and successful/fallback `hid_open_path` branches.
OS success, ready ACK, allocation/CRT helpers and model flags are supplied
inputs. The open metadata helper is a separately inspected boundary. These
checks do not emulate a receiver or prove physical USB packetization.

## Exact boundaries

| Path | Verified behavior |
| --- | --- |
| OEM prepare `0x42043F..0x4204B4` | Constructs `A0 0B 08 00 01 00 00 00`; its HIDAPI call requests logical length 8. |
| HIDAPI send `0x100020C0..0x10002117` | Reads feature capacity from device+0x14; copies 8 bytes and explicitly zeroes 126 bytes before one `HidD_SetFeature(...,134)`. Returns the caller's original length 8. Scratch was deliberately nonzero in the fixture. |
| hiddriver SetFeature `0x10002340` | Forwards buffer/length directly to `HidD_SetFeature`; eight bytes remain 8. There is no hidden padding or wake command. |
| HIDAPI GET `0x10002140` | Uses `DeviceIoControl` code `0xB0192`, input 8/output 8 for `A0 00 00 00 00 00 00 00`, then `GetOverlappedResult(wait=TRUE)`. GET does not pad to 134. |
| OEM normal HIDAPI helper | Sleeps 100 ms before one prepare, 500 ms afterward, then observes one ACK. It retries prepare only for a reported transport-send failure; this behavior is not adopted by our probe. |

The radio route is a static model-state finding: OEM enumeration uses
`1D57:FA60` at `0x40D898`; receiver `03 C9` input sets the G9 model's +0x420
connected flag through `0x40E16F` / `0x41F9ED`. The constructor records wired
2148 and radio FA60 at `0x417B9A..0x417BB5`. An active +0x420 flag enters the
normal A0 branch; absent wired +0x410==1, it uses HIDAPI. Separate +0x424/+0x428
paths use wrapped 64-byte `hid_write` envelopes and are not interchangeable.
This is not a captured live OEM execution trace.

Earlier `evidence/rf-settings-resume/radio-firmware.log`, `radio-profile.log`
and `radio-settings.log` prove that our eight-byte direct prepare previously
succeeded with the same logical requests. They exclude a claim that the
unpadded prepare is universally unsupported. No missing wake/reset command
was found in the inspected normal prepare/open paths.

## Open/GET distinction

The pinned HIDAPI is PE32 x86. Its exports use stdcall:
`_hid_open_path@4`, `_hid_get_feature_report@12`, `_hid_close@4`.
Open requests read/write access with share 3, OPEN_EXISTING and OVERLAPPED;
its fallback uses access 0 while retaining OVERLAPPED. It creates host events,
sets the host input-buffer count to 64, and reads descriptors/metadata. No
feature SET, named payload GET, device reset or wake is part of that open path.
The existing `G9Hid.Open` uses access 0 without OVERLAPPED. The physical effect
of that distinction was not established. The native HIDAPI GET has no internal
timeout: a diagnostic caller needs an external process deadline.

## Controlled physical result and current state

After owner reset and fresh enumeration showing only FA60, a private one-shot
probe sent the **same** logical version request plus 126 zero bytes, retained
eight-byte bounded A0 polling, and permitted one GET0B of 64 bytes only if granted. This
used direct HidD with the fixture-proven equivalent padding, not a live OEM
send-DLL call. It logged fourteen idle ACKs and stopped without a payload GET.

One subsequent ACK-only call through the pinned original x86 HIDAPI returned
length 8 / `A0 00 00 00 00 00 00 00` for that pending request. It sent no prepare
or named payload request. Evidence:
`evidence/flash-experiment/20261116T2031390956600Z-DG2-bounded/radio-oem-padding-20261116T2050509747298Z/firmware-padded.log`
and `oem-ack-only.log`, with their checkpoints.

Padding alone therefore failed to resolve this instance, and the original
HIDAPI GET agreed that the receiver was not ready. Neither observation proves
whether the mouse received/prepared the request. No further retry, response
drain, setting/OLED write or production transport change follows from this
audit. The physical cause remains unknown.

A later owner-confirmed reset preceded one complete version transaction through
the pinned original x86 HIDAPI: its read/write OVERLAPPED open, native send
(logical return value 8, padded internally), a 500 ms first-ACK delay, and
bounded native ACK reads. It also timed out with every A0 reply not-ready.
No version payload was consumed; the helper exited normally within its external
six-second process limit. The attempt is recorded under
`evidence/flash-experiment/20261116T2031390956600Z-DG2-bounded/radio-full-oem-20261116T2100351758215Z/`.

Thus using the original library did not repair this instance either. This is
not a capture of the entire OEM application's initialization or a proof of which
device-side state failed. The next diagnostic should observe the mouse's RF
state, rather than repeat transport-format variants.
