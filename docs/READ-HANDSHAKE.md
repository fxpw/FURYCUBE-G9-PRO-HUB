# Bounded A0 readiness wait

Offline audit, 2026-11-09. A full wired settings read succeeded, but the next settings update stopped **before any settings SET**: its fourth prepare (`A0 06 09 00 01 00 00 00`) still returned `A0 00 00 00 00 00 00 00` at the fixed 500 ms check. The observation proves not-ready at that check, not permanent failure or a confirmed timing-only cause. Evidence: local `full-settings-read-20261109.log` and `full-settings-set11-20261109.log`.

## Stock firmware consumption proof

The original, unpatched firmware distinguishes ACK observation from payload consumption:

- HID GET dispatch at `0x38938..0x3893E` branches on report ID `0xA0`.
- For **GET A0**, `0x3898C..0x389B0` writes the ACK buffer: byte 0 is A0, byte 1 is 0/1 derived from the ready byte at RAM `0x400B15`, bytes 2–7 are zero. It does not clear ready, submit a new named-record query, run `Get_Report_Data`, load a profile, or call the wake routine. Normal EP0 bookkeeping/ACK scratch writes still occur.
- For a **non-A0 GET**, `0x38940..0x38948` first requires ready and then clears it. `0x3894A..0x3898A` submits the prepared response once. Thus it is the payload GET, not ACK GET, which consumes readiness.
- The asynchronous read FSM `0x34216` clears ready when starting a query (`0x34294..0x34298`), waits for profile work where required, constructs the reply at `0x34308`, sets ready at `0x3430E`, and waits for consumption at `0x34312..0x3431A` before returning to idle.

An offline Unicorn check executed the **unpatched** stock instructions using `FlowFixture(b'', {}, patched=False)` from the existing fixture tools. Four consecutive GET A0 calls with ready=0 and four with ready=1 preserved READY, FSM, request context, prepared response and configuration RAM. The fixture recorded zero wake calls, zero flash reads/writes and zero configuration writes for those calls. One subsequent GET 0x06 returned the synthetic prepared nine-byte polling response and cleared ready. USB hardware and ROM transfer helpers were explicitly stubbed: this is instruction-path evidence, not physical-device testing.

## Native OEM host timing

The original HUB read-prepare helper at `0x420300` waits 100 ms initially (`0x42046A`). Its wired SetFeature path waits another 100 ms after send (`0x420499..0x4204C6`); an alternate HIDAPI path waits 500 ms. A send-transport failure can cause that OEM helper to resend up to ten times (`0x4204D2..0x4204E0`). After transport success it performs **one** GET A0 and tests byte 1 (`0x4204E2..0x420524`), with another 100 ms sleep. This supplies no guarantee that a response is always ready within 500 ms. The new policy does not copy its resend behavior.

## Host policy and integration

`src/G9ReadHandshake.cs` has no send or payload-consumption callback. Its only transport access is a supplied GET A0 delegate. This prevents it from resending prepare requests or draining responses:

1. Check the initial ACK once with `ValidateIdle`; an already-ready response is stale and aborts before prepare.
2. Send the already validated prepare exactly once.
3. Call `WaitForReady` with the same open target/session, a monotonic clock, and a delay delegate. The settings transport chooses 2000 ms overall, 100 ms polls, 500 ms initial wait; the generic default deadline is 3000 ms.
4. Only exact eight-byte `A0 00` plus zero padding is pending. Only exact `A0 01` plus zero padding is ready. Wrong ID/state/length/padding or transport errors abort immediately without retry.
5. Once ready, consume the expected payload exactly once and retain the caller's independent ID, transfer length, selector/profile and checksum checks. A ready ACK alone does not identify a response.
6. Deadline expiry stops the transaction. Do not resend prepare, consume an ungranted response, automatically retry the transaction, or continue settings writes. Blocking OS calls themselves are not cancellable by this pure helper; a ready ACK arriving after the measured deadline is rejected.

The helper additionally bounds polls by scheduled waits, so a broken/nonadvancing clock or wait callback cannot create an infinite GET loop. A clock that moves backwards aborts.

**Important:** ready=0 is not proof that the firmware FSM is idle; it also occurs while a previous request is still preparing. Reopening a Windows handle does not reset firmware state. After a timeout, malformed ACK or transport failure, callers must stop the session and require a separately established recovery/reset before another prepare. Serialize all configuration/OLED queries; ACK polling cannot supply a transaction identifier or fix overlapping requests.

Pure tests: `tools\build-read-handshake-tests.cmd` — **PASS 35**. Coverage includes delayed readiness, exact-deadline readiness, timeout, malformed/stale ACKs, transport failures, late blocking replies, no-consume/no-resend transaction sequencing, frozen/backward clocks and policy validation. No USB was used by this audit or these tests.

## Subsequent live observation: ready does not establish body freshness

The root agent subsequently ran an explicitly authorized settings update with the bounded wait. Its complete baseline read passed; the known SET 0x05 changed standby from 10 to 11 minutes, and the first full-settings response after that write contained the expected new value. Later in the same readback sequence, after successful individual sensor/polling/RF/key reports, a granted GET 0x0A returned a mismatched body. The strict settings decoder aborted rather than publishing that readback as success. Local evidence: `full-settings-set11-readywait-20261109.log`.

An offline byte comparison establishes the exact mismatch:

```text
failed 120-byte response = 0A 78 01
                        + all 56 bytes of previous GET 08 response after its 3-byte header
                        + 61 zero bytes
```

The 56 bytes include the previous key-table checksum. This is an exact previous key-response body under a new all-data header, not merely an unexpected DPI value or a field conversion error.

Relevant static ordering is visible, but does not establish the precise cause of this serialized live failure:

- Prepare processing at `0x34294..0x342B0` clears ready and writes the new response report/length/selector header **before** asynchronous work and the later `Get_Report_Data` call.
- GET A0 exposes only one ready bit. It carries no selected-report identity, request generation or body hash.
- The shared request context at `0x400F88` is mutable in several stages: A0 OUT completion at `0x37852..0x3787C` changes selected report/length/selector and queues work without testing the read-FSM state; non-A0 GET at `0x3894E..0x3896A` changes context report/pointer while consuming ready.
- Non-A0 GET additionally sets `0x400B26`, a completion flag; this is **not** the queued-query flag `0x400B14`. Treating these two flags as identical would be an incorrect race explanation.
- FSM consumption completion is checked later at `0x34312..0x3431A`. A host-observed ready=0 does not expose that final state transition.

These facts explain why a ready bit and a correct header are insufficient validation. They do not prove that a particular sleep duration, retransmission, extra GET or host-side generation counter repairs the observed body-publication issue. No such fix or physical recovery behavior was inferred, and no speculative commands were sent by the audit agent. ACK-only bounded waiting addresses delayed readiness; it does **not** solve or mask this independent freshness failure. Retain exact body/checksum/cross-record consistency checks and stop on mismatch.

The root agent then restored the known original setting using the existing settings transaction, and a complete readback passed. It compared the full 117-byte logical body, key table and raw profile response with the initial backup: all matched; debounce remained 8 ms. Evidence: `full-settings-restore10-20261109.log`. This establishes logical-settings restoration for the verified records, not a physical flash dump or firmware-recovery proof. Further write experiments were stopped.
