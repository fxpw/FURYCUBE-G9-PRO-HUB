/* DG1: OEM diagnostic baseline, no custom OLED/version/transport hooks.
 * Exact self-contained OL7 EP0 mouse-readiness leaf, unchanged instructions.
 * Only this leaf and the separate existing NoWait NOP are installed. */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4
.global g9_dg1_ep0_mouse_ready_hook
.type g9_dg1_ep0_mouse_ready_hook,%function
.thumb_func
g9_dg1_ep0_mouse_ready_hook:
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
.size g9_dg1_ep0_mouse_ready_hook,.-g9_dg1_ep0_mouse_ready_hook
.balign 4
