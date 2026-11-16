/* OL4: fixed 64-byte EP0 OUT chunks. Stock USB OUT cannot reassemble132B.
 * One pinned EP0 mouse-ready guard; no OUT reassembly replacement,
 * persistent globals or new NVM writes.
 * GO4 chunk40px; GS4 status64B; descriptor22 patched to63 payload bytes.
 */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4
.equ REQUEST_CONTEXT, 0x00400f88
.equ WIRED_PAYLOAD, 0x00400f93
.equ STOCK_WAKE, 0x00034af9
.equ STOCK_MEMCPY, 0x00024f9d
.equ STOCK_OLED_INIT, 0x0003cddf
.equ STOCK_DRAW, 0x0003cee7

.global g9_ol4_set_hook
.type g9_ol4_set_hook,%function
.thumb_func
g9_ol4_set_hook:
    ldr r3, =REQUEST_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #0x22
    beq .Lcustom
    ldr r3, =STOCK_WAKE
    bx r3
.Lcustom:
    push {r4, r5, r6, r7, lr}
    sub sp, #68
    ldrb r0, [r3, #3]
    cmp r0, #64
    bne .Ldone_early
    ldr r1, [r3, #4]
    ldr r0, =WIRED_PAYLOAD
    cmp r1, r0
    bne .Ldone_early
    subs r1, #2
    mov r0, sp
    movs r2, #64
    ldr r3, =STOCK_MEMCPY
    blx r3
    mov r4, sp
    ldrb r0, [r4]
    cmp r0, #0x22
    bne .Ldone_early
    ldrb r0, [r4, #1]
    cmp r0, #64
    bne .Ldone_early
    ldrb r0, [r4, #2]
    cmp r0, #0x47
    bne .Ldone_early
    ldrb r0, [r4, #3]
    cmp r0, #0x4f
    bne .Ldone_early
    ldrb r0, [r4, #4]
    cmp r0, #4
    bne .Ldone_early
    ldrb r0, [r4, #5]
    cmp r0, #1
    bne .Ldone_early
    ldrb r0, [r4, #6]
    cmp r0, #0
    bne .Ldone_early
    ldrb r5, [r4, #7]
    cmp r5, #3
    bhi .Ldone_early
    ldrb r6, [r4, #8]
    cmp r6, #0
    beq .Lx_ok
    cmp r6, #40
    beq .Lx_ok
    cmp r6, #80
    bne .Ldone_early
.Lx_ok:
    ldrb r0, [r4, #9]
    cmp r0, #40
    bne .Ldone_early
    ldrb r7, [r4, #10]
    cmp r7, #1
    bhi .Ldone_early
    beq .Linit_flag
    b .Lflag_ok
.Linit_flag:
    movs r0, r5
    orrs r0, r6
    bne .Ldone_early
.Lflag_ok:
    ldrb r0, [r4, #11]
    cmp r0, #0
    bne .Ldone_early
    b .Lcheck_tail
.Ldone_early:
    b .Ldone
.Lcheck_tail:
    movs r0, r4
    adds r0, #52
    movs r1, #10
.Lzero_tail:
    ldrb r2, [r0]
    cmp r2, #0
    bne .Ldone
    adds r0, #1
    subs r1, #1
    bne .Lzero_tail
    movs r0, r4
    movs r1, #62
    bl .Lcrc16
    movs r1, r4
    adds r1, #62
    ldrb r2, [r1]
    ldrb r1, [r1, #1]
    lsls r1, #8
    orrs r1, r2
    cmp r0, r1
    bne .Ldone
    cmp r5, #3
    bne .Lvalid
    movs r0, r4
    adds r0, #12
    movs r1, #40
    movs r3, #0xf0
.Llast_page:
    ldrb r2, [r0]
    tst r2, r3
    bne .Ldone
    adds r0, #1
    subs r1, #1
    bne .Llast_page
.Lvalid:
    ldr r3, =STOCK_WAKE
    blx r3
    cmp r7, #0
    beq .Ldraw
    ldr r3, =STOCK_OLED_INIT
    blx r3
.Ldraw:
    movs r0, r6
    movs r1, r5
    movs r2, #40
    movs r3, r4
    adds r3, #12
    ldr r7, =STOCK_DRAW
    blx r7
.Ldone:
    add sp, #68
    pop {r4, r5, r6, r7, pc}
.size g9_ol4_set_hook, .-g9_ol4_set_hook
.balign 4
.ltorg

/* CRC-16/CCITT-FALSE, r0=pointer, r1=count -> r0=CRC. No ROM dependency. */
.type .Lcrc16,%function
.thumb_func
.Lcrc16:
    push {r4, r5, r6, r7, lr}
    movs r4, r0
    movs r6, r1
    ldr r5, =0xffff
    ldr r7, =0x1021
.Lbyte:
    ldrb r0, [r4]
    adds r4, #1
    lsls r0, r0, #8
    eors r5, r0
    movs r1, #8
.Lbit:
    lsls r2, r5, #16
    bpl .Lno_xor
    lsls r5, r5, #1
    eors r5, r7
    b .Ltruncate
.Lno_xor:
    lsls r5, r5, #1
.Ltruncate:
    lsls r5, r5, #16
    lsrs r5, r5, #16
    subs r1, #1
    bne .Lbit
    subs r6, #1
    bne .Lbyte
    movs r0, r5
    pop {r4, r5, r6, r7, pc}
.balign 4
.ltorg

.equ OL4_CONTEXT, 0x00400f88
.equ OL4_RESPONSE, 0x00400f91
.equ OL4_BODY, 0x00400f93
.equ OL4_QUERY, 0x004011c8  /* Stock EP0 OUT staging: 0x40124D - 0x85. */
.equ OL4_STOCK_GET, 0x00033b85

.global g9_ol4_version_hook
.type g9_ol4_version_hook,%function
.thumb_func
g9_ol4_version_hook:
    strb r1, [r2]
    ldrb r0, [r0, #1]
    movs r3, #0x4f
    strb r3, [r2, #2]
    movs r3, #0x4c
    strb r3, [r2, #3]
    movs r3, #0x34
    strb r3, [r2, #4]
    bx lr
.size g9_ol4_version_hook, .-g9_ol4_version_hook

/* Hook 0x342A4, replacing STRB r2,[r0,#11] / BEQ 0x342B2.
 * The idle FSM samples report/selector before checking its pending flag.
 * An EP0 OUT IRQ between those operations can queue a new request while
 * r1/r2 still describe the previous response. The flag has been claimed and
 * cleared at 0x3424A..4C by this point: reload its report/length/selector here.
 * A serialized host sends no new A0 before consuming this request; GET A0
 * does not replace these parameters. This does not support concurrent owners.
 * All report22 reads, including malformed ones, skip selector-based preload
 * and the normal clearing of macro/profile selectors. Other IDs retain their
 * original selector-zero/nonzero behavior with the claimed request's values.
 * r0=context,r3=body,r4=FSM,r6=0; r2 is replaced with the current selector.
 */
.global g9_ol4_prepare_hook
.type g9_ol4_prepare_hook,%function
.thumb_func
g9_ol4_prepare_hook:
    push {r1}
    ldrb r1, [r0, #2]
    strb r1, [r0, #9]
    ldr r2, =0x00400b9f
    ldrb r2, [r2]
    strb r2, [r0, #11]
    ldr r1, =0x00400bb0
    ldrb r1, [r1]
    strb r1, [r0, #10]
    ldrb r1, [r0, #2]
    cmp r1, #0x22
    beq .Lprepare_custom
    pop {r1}
    cmp r2, #0
    beq .Lprepare_legacy_zero
    bx lr
.Lprepare_legacy_zero:
    ldr r1, =0x000342b3
    bx r1
.Lprepare_custom:
    pop {r1}
    str r3, [r0, #4]
    movs r0, #3             /* Build status next tick; pending work yields BUSY. */
    ldr r1, =0x000342bf      /* Shared FSM state store/return, no selector clear. */
    bx r1
.size g9_ol4_prepare_hook, .-g9_ol4_prepare_hook

/* Hook 0x34308, replacing BL Get_Report_Data. Every22 request returns through
 * this handler; only the exact wired selector0/length64 request may be valid.
 * Stock A0 transport wake remains, but no extra wake/profile restore occurs.
 */
.global g9_ol4_get_hook
.type g9_ol4_get_hook,%function
.thumb_func
g9_ol4_get_hook:
    ldr r3, =OL4_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #0x22
    beq .Lstatus
    ldr r3, =OL4_STOCK_GET
    bx r3
.Lstatus:
    push {r4, r5, r6, r7, lr}
    movs r7, #0             /* Invalid unless the exact request is proved. */
    ldrb r0, [r3, #1]
    cmp r0, #2
    bne .Lclear
    ldrb r0, [r3, #10]
    cmp r0, #64
    bne .Lclear
    ldrb r0, [r3, #11]
    cmp r0, #0
    bne .Lclear
    ldr r0, [r3, #4]
    ldr r1, =OL4_BODY
    cmp r0, r1
    bne .Lclear
    ldr r0, =0x00400b28
    ldrb r0, [r0]
    cmp r0, #0
    bne .Lclear
    ldr r0, =0x00400a7c
    ldrb r0, [r0]
    cmp r0, #0
    beq .Lclear
    ldr r0, =OL4_QUERY
    ldrb r1, [r0]
    cmp r1, #0xa0
    bne .Lclear
    ldrb r1, [r0, #1]
    cmp r1, #0x22
    bne .Lclear
    ldrb r1, [r0, #2]
    cmp r1, #64
    bne .Lclear
    adds r0, #3
    movs r2, #5
.Lquery_zero:
    ldrb r1, [r0]
    cmp r1, #0
    bne .Lclear
    adds r0, #1
    subs r2, #1
    bne .Lquery_zero
    movs r7, #1
.Lclear:
    ldr r4, =OL4_RESPONSE
    movs r0, #0
    movs r1, #64
    movs r2, r4
.Lzero:
    strb r0, [r2]
    adds r2, #1
    subs r1, #1
    bne .Lzero
    movs r0, #0x22
    strb r0, [r4]
    movs r0, #64
    strb r0, [r4, #1]
    movs r0, #0x47
    strb r0, [r4, #2]
    movs r0, #0x53
    strb r0, [r4, #3]
    movs r0, #4
    strb r0, [r4, #4]
    cmp r7, #0
    beq .Lcrc

    /* Main-loop execution excludes concurrent main-thread record copies.
     * Pending work must not turn a half-loaded profile into a valid value. */
    ldr r0, =0x00400b2d
    ldrb r1, [r0]
    ldrb r2, [r0, #1]
    orrs r1, r2
    bne .Lbusy
    ldr r0, =0x00400aa0
    ldrb r1, [r0]
    ldrb r2, [r0, #1]
    orrs r1, r2
    ldrb r2, [r0, #2]
    orrs r1, r2
    bne .Lbusy
    ldr r0, =0x00400b14
    ldrb r0, [r0]
    cmp r0, #0
    bne .Lbusy

    ldr r0, =0x00401020
    ldrb r5, [r0, #12]
    cmp r5, #4
    bhi .Lcrc
    ldr r1, =0x00400b27
    ldrb r1, [r1]
    cmp r1, r5
    bne .Lbusy
    adds r0, #0x20
    ldrb r6, [r0, #15]
    cmp r6, #1
    blo .Lcrc
    cmp r6, #8
    bhi .Lcrc
    subs r0, #2            /* Exactly OLED: [40103E+stage],[401046+stage]. */
    ldrb r7, [r0, r6]
    adds r0, #8
    ldrb r0, [r0, r6]
    lsls r0, #8
    orrs r7, r0
    beq .Lcrc              /* Zero does not authorize an invented DPI. */
    movs r0, #1
    strb r0, [r4, #5]
    adds r5, #1
    strb r5, [r4, #6]
    strb r6, [r4, #7]
    strb r7, [r4, #8]
    lsrs r7, #8
    strb r7, [r4, #9]
    b .Lcrc
.Lbusy:
    movs r0, #2
    strb r0, [r4, #5]
.Lcrc:
    movs r0, r4
    movs r1, #62
    bl .Lcrc16             /* Local CRC routine, no added ROM calls. */
    adds r4, #62
    strb r0, [r4]
    lsrs r0, #8
    strb r0, [r4, #1]
    pop {r4, r5, r6, r7, pc}
.size g9_ol4_get_hook, .-g9_ol4_get_hook
.balign 4
.ltorg

/* Hook 0x37896, replacing STRB r6,[r0] / LDRB r2,[r5].
 * The stock EP0 OUT callback unconditionally marks mouse endpoint2 ready,
 * even when it is still transmitting its previous input report. Frequent
 * display chunks must not impersonate endpoint2's actual IN completion.
 * r1 already points to the received packet (SP+4, set at 0x37894).
 * Report22 is OL4's dedicated channel, including malformed custom packets.
 * Other report IDs retain the stock store; the real EP2 callback 0x387A0
 * remains untouched. This leaf preserves every original live register.
 */
.global g9_ol4_ep0_mouse_ready_hook
.type g9_ol4_ep0_mouse_ready_hook,%function
.thumb_func
g9_ol4_ep0_mouse_ready_hook:
    push {r3}
    ldrb r3, [r1]
    cmp r3, #0x22
    beq .Lep0_keep_mouse_ready
    strb r6, [r0]
.Lep0_keep_mouse_ready:
    ldrb r2, [r5]
    pop {r3}
    bx lr
.size g9_ol4_ep0_mouse_ready_hook, .-g9_ol4_ep0_mouse_ready_hook
.balign 4
.ltorg
