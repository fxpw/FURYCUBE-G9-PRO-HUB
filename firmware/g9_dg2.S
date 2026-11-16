/* DG2: OEM diagnostic baseline, normal USB-read metadata fix; no custom OLED/version/RF hooks.
 * Exact self-contained OL7 EP0 mouse-readiness leaf, unchanged instructions.
 * Only this leaf, the normal claimed-metadata leaf and existing NoWait NOP. */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4
.global g9_dg2_ep0_mouse_ready_hook
.type g9_dg2_ep0_mouse_ready_hook,%function
.thumb_func
g9_dg2_ep0_mouse_ready_hook:
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
.size g9_dg2_ep0_mouse_ready_hook,.-g9_dg2_ep0_mouse_ready_hook
.balign 4

/* Replace STRB selector / BEQ at0x342A4. Reload only the metadata belonging
 * to the pending A0 request already claimed by the idle FSM. All report IDs
 * follow the stock selector-zero/nonzero paths; no custom22 branch exists. */
.global g9_dg2_prepare_hook
.type g9_dg2_prepare_hook,%function
.thumb_func
g9_dg2_prepare_hook:
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
.size g9_dg2_prepare_hook,.-g9_dg2_prepare_hook
.balign 4
.ltorg
