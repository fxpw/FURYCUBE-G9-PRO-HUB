/* DG3: DG2 OEM baseline plus display-only RF state telemetry.
 * Retains EP0 readiness leaf, normal claimed-metadata fix and NoWait NOP.
 * Two added callsites: native display scalar and RF-parser wake wrapper.
 * No custom OLED upload, RF protocol/getter/version changes or new RAM. */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4
.global g9_dg3_ep0_mouse_ready_hook
.type g9_dg3_ep0_mouse_ready_hook,%function
.thumb_func
g9_dg3_ep0_mouse_ready_hook:
    push {r3}
    ldrb r3, [r1]
    cmp r3, #4
    beq .Lep0_keep_mouse_ready
    cmp r3, #5
    beq .Lep0_keep_mouse_ready
    cmp r3, #6
    beq .Lep0_keep_mouse_ready
    cmp r3, #8
    beq .Lep0_keep_mouse_ready
    cmp r3, #12
    beq .Lep0_keep_mouse_ready
    cmp r3, #0x22
    beq .Lep0_keep_mouse_ready
    strb r6, [r0]
.Lep0_keep_mouse_ready:
    ldrb r2, [r5]
    pop {r3}
    bx lr
.size g9_dg3_ep0_mouse_ready_hook,.-g9_dg3_ep0_mouse_ready_hook
.balign 4

/* Replace STRB selector / BEQ at0x342A4. Reload only the metadata belonging
 * to the pending A0 request already claimed by the idle FSM. All report IDs
 * follow the stock selector-zero/nonzero paths; no custom22 branch exists. */
.global g9_dg3_prepare_hook
.type g9_dg3_prepare_hook,%function
.thumb_func
g9_dg3_prepare_hook:
    push {r1}
    ldrb r1, [r0, #2]
    strb r1, [r0, #9]
    ldr r2, =0x00400b9f
    ldrb r2, [r2]
    strb r2, [r0, #11]
    ldr r1, =0x00400bb0
    ldrb r1, [r1]
    strb r1, [r0, #10]
    pop {r1}
    cmp r2, #0
    beq .Lprepare_legacy_zero
    bx lr
.Lprepare_legacy_zero:
    ldr r1, =0x000342b3
    bx r1
.size g9_dg3_prepare_hook,.-g9_dg3_prepare_hook
.balign 4
.ltorg

/* Display-only numeric source. Every caller already chose to draw; no extra
 * wake/refresh, mode changes, DPI or sensor writes. r0 is dead before the
 * next threshold path assigns it. The native CMP at3D33E defines flags. */
.global g9_dg3_display_scalar
.type g9_dg3_display_scalar,%function
.thumb_func
g9_dg3_display_scalar:
    ldr r2, =0x00400b5c
    ldrb r2, [r2]
    ldr r0, =9000
    adds r2, r2, r0
    bx lr
.size g9_dg3_display_scalar,.-g9_dg3_display_scalar
.balign 4
.ltorg

/* ONLY the native RF parser wake BL305D2 uses this wrapper. It executes wake
 * first, then queues A83 for accepted GET0B/length8 (RFBUF now47/0B/08).
 * RFmode is established by the original caller; stock wake clears A7A.
 * No AB6/B2F writes, draws, timer, new RAM or new RF behavior. AF1/AF2 block
 * the queued redraw. r0-r3 and all callee registers retain native outputs.
 * Native continuation overwrites flags before branching; no flag is live. */
.global g9_dg3_radio_wake_hook
.type g9_dg3_radio_wake_hook,%function
.thumb_func
g9_dg3_radio_wake_hook:
    push {r4,lr}
    ldr r4, =0x00034af9
    blx r4
    push {r0,r1,r2,r3}
    ldr r2, =0x00400af1
    ldrb r1, [r2]
    ldrb r0, [r2,#1]
    orrs r1,r0
    bne .Lwake_done
    ldr r0, =0x00400eef
    ldrb r1, [r0]
    cmp r1, #0x47
    bne .Lwake_done
    ldrb r1, [r0,#1]
    cmp r1, #0x0b
    bne .Lwake_done
    ldrb r1, [r0,#2]
    cmp r1, #8
    bne .Lwake_done
    subs r2, #0x6e
    movs r1, #1
    strb r1, [r2]
.Lwake_done:
    pop {r0,r1,r2,r3}
    pop {r4,pc}
.size g9_dg3_radio_wake_hook,.-g9_dg3_radio_wake_hook
.balign 4
.ltorg
