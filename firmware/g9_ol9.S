/* OFFLINE OL9 prototype, not an installed or packaged update.
 * USB uses fixed 64-byte EP0 OUT chunks. RF experiments use a reserved
 * checksum-compatible SET04 envelope; physical receiver delivery is unproven.
 * One pinned EP0 mouse-ready guard; retained SRAM frame with native ownership.
 * No OUT reassembly replacement or new NVM writes.
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
.ifdef G9_OLED_RADIO_ACK
.equ RADIO_ACK_GENERATION, 28
.endif
.ifdef G9_OLED_RADIO_RETRY
.equ RADIO_ACK_DELTA_GENERATION, 32
.equ RADIO_ACK_DELTA_SIGNATURE, 36
.endif
.ifdef G9_OLED_DIRTY_DRAW
.equ DELTA_DIRTY_MASK, 24 /* Aligned halfword in the reserved store metadata. */
.endif
.ifdef G9_EMBEDDED_OLED
.equ LOCAL_OWNER, 20
.equ LOCAL_LEVEL, 21
.equ LOCAL_DRAWN_LEVEL, 22
.endif

.global g9_ol9_set_hook
.type g9_ol9_set_hook,%function
.thumb_func
g9_ol9_set_hook:
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
.Lvalidate:
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
    movs r0,r5
    lsls r0,#1
    adds r0,r0,r5
    cmp r6,#0
    beq .Lstore_index
    adds r0,#1
    cmp r6,#40
    beq .Lstore_index
    adds r0,#1
.Lstore_index:
    movs r1,r4
    adds r1,#12
    bl .Lstore_chunk_entry
    cmp r0,#2
    beq .Lcommit_wake
    cmp r0,#1
    bne .Ldone
    movs r0,r5
    orrs r0,r6
    bne .Ldone
    bl .Llookup
    cmp r0,#0
    beq .Ldone
    ldr r1,=META
    adds r0,r0,r1
    strb r7,[r0,#17]
    b .Ldone
.Lcommit_wake:
    ldr r0,=0x00400af2
    ldrb r0,[r0]
    cmp r0,#0
    bne .Ldone
    ldr r0,=0x00400a7a
    ldrb r6,[r0]
    cmp r6,#0
    beq .Lcall_commit_wake
    /* Initialization from earlier this boot is stale after sleep. Native
     * wake may skip init under AF1; only its actual final-AF hook can prove
     * that this wake initialized the panel. */
    bl .Llookup
    cmp r0,#0
    beq .Lcall_commit_wake
    ldr r1,=META
    adds r0,r0,r1
    movs r1,#0
    strb r1,[r0,#19]
.Lcall_commit_wake:
    ldr r3,=STOCK_WAKE
    blx r3
    bl .Llookup
    cmp r0,#0
    beq .Ldone
    ldr r1,=META
    adds r0,r0,r1
    ldrb r0,[r0,#17]
    cmp r0,#0
    beq .Ldone
    bl .Llookup
    ldr r1,=META
    adds r0,r0,r1
    ldrb r0,[r0,#19]
    cmp r0,#1
    beq .Ldone
    ldr r3,=STOCK_OLED_INIT
    blx r3
.Ldone:
    add sp, #68
    pop {r4, r5, r6, r7, pc}
.size g9_ol9_set_hook, .-g9_ol9_set_hook
.balign 4
.ltorg

/* Linked into this same text section; all private store labels stay local. */
.include "firmware/g9_screen_store.S"

/* Only pinned pixel BL callsites use this gate. Command/address/control
 * calls retain their original targets and therefore keep stock power policy.
 * The allowed path executes the original byte primitive. The suppressed
 * path reproduces its return registers/NZCV without touching GPIO.
 */
.global g9_ol9_pixel_gate
.type g9_ol9_pixel_gate,%function
.thumb_func
g9_ol9_pixel_gate:
    push {r0,r1,r2,r3,r4,lr}
    bl .Llookup
    cmp r0,#0
    beq .Lpixel_allow
    ldr r1,=META
    adds r0,r0,r1
    ldrb r1,[r0,#1]
    cmp r1,#1
    bne .Lpixel_allow
    ldrb r1,[r0,#4]
    cmp r1,#1
    bne .Lpixel_allow
    ldrb r1,[r0,#5]
    cmp r1,#1
    beq .Lpixel_allow
    add sp,#16
    pop {r4}
    pop {r1}
    mov lr,r1
    movs r0,#0
    ldr r1,=0x00800a40
    movs r2,#0
    movs r3,#2
    cmp r0,r0
    bx lr
.Lpixel_allow:
    pop {r0,r1,r2,r3,r4}
    pop {r3}
    mov lr,r3
    ldr r3,=0x0003cd7b
    bx r3
.size g9_ol9_pixel_gate,.-g9_ol9_pixel_gate
.balign 4
.ltorg

/* Final AF command in the real initialization routine. */
.global g9_ol9_init_done_hook
.type g9_ol9_init_done_hook,%function
.thumb_func
g9_ol9_init_done_hook:
    push {r4,lr}
    ldr r3,=0x0003cdbf
    blx r3
    bl .Llookup
    cmp r0,#0
    beq .Linit_done
    ldr r1,=META
    adds r0,r0,r1
    ldrb r1,[r0,#4]
    strb r1,[r0,#6]
    movs r1,#0
    strb r1,[r0,#16]
.ifdef G9_OLED_DIRTY_DRAW
    strh r1,[r0,#DELTA_DIRTY_MASK]
.endif
    movs r1,#1
    strb r1,[r0,#19]
.Linit_done:
    pop {r4,pc}
.size g9_ol9_init_done_hook,.-g9_ol9_init_done_hook
.balign 4
.ltorg

/* Both RF and USB main-loop callsites previously called355B8. Flags are
 * call-clobbered at these ABI boundaries; mode and IRQ/FIQ bits stay intact.
 */
.global g9_ol9_loop_hook
.type g9_ol9_loop_hook,%function
.thumb_func
g9_ol9_loop_hook:
    push {r4,lr}
    ldr r3,=0x000355b9
    blx r3
    bl .Lrestore_tick_entry
    pop {r4,pc}
.size g9_ol9_loop_hook,.-g9_ol9_loop_hook
.balign 4
.ltorg

/* At most ONE40-byte page chunk per main-loop iteration. Never wakes a
 * sleeping/user-disabled display. No malloc, CRC or NVM in this renderer.
 */
.global g9_ol9_restore_tick
.type g9_ol9_restore_tick,%function
.thumb_func
g9_ol9_restore_tick:
.Lrestore_tick_entry:
    push {r4,r5,r6,r7,lr}
    sub sp,#4
    bl .Llookup
    cmp r0,#0
    beq .Ltick_done
    movs r5,r0
    ldr r1,=META
    adds r4,r0,r1
    ldrb r0,[r4,#1]
    cmp r0,#1
.ifdef G9_EMBEDDED_OLED
    bne .Llocal_restore
    ldrb r0,[r4,#LOCAL_OWNER]
    cmp r0,#1
    beq .Llocal_restore
.else
    bne .Ltick_done
.endif
    ldrb r0,[r4,#4]
    cmp r0,#1
    bne .Ltick_done
    ldrb r0,[r4,#6]
    cmp r0,#1
    bne .Ltick_done
    ldr r0,=0x00400a7a
    ldrb r0,[r0]
    cmp r0,#0
.ifdef G9_OLED_DIRTY_DRAW
    bne .Ldraw_paused
.else
    bne .Ltick_done
.endif
    ldr r0,=0x00400af2
    ldrb r0,[r0]
    cmp r0,#0
.ifdef G9_OLED_DIRTY_DRAW
    bne .Ldraw_paused
.else
    bne .Ltick_done
.endif
    ldrb r6,[r4,#16]
    cmp r6,#11
    bhi .Ltick_done
.ifdef G9_OLED_DIRTY_DRAW
    ldrh r7,[r4,#DELTA_DIRTY_MASK]
    cmp r7,#0
    beq .Ldraw_chunk
.Lnext_dirty_chunk:
    movs r0,#1
    lsls r0,r6
    tst r7,r0
    bne .Ldraw_chunk
    adds r6,#1
    cmp r6,#12
    blo .Lnext_dirty_chunk
    b .Lredraw_done
.Ldraw_chunk:
.endif
    movs r0,r6
    movs r1,#0
.Lpage_div:
    cmp r0,#3
    blo .Lpage_ready
    subs r0,#3
    adds r1,#1
    b .Lpage_div
.Lpage_ready:
    movs r2,#40
    muls r0,r2
    movs r3,#40
    muls r3,r6
    adds r3,r3,r5
    adds r3,#4
    ldrb r7,[r4]
    cmp r7,#0
    beq .Lactive_pixels
    movs r7,#240
    lsls r7,#1
    adds r3,r3,r7
.Lactive_pixels:
    movs r7,#1
    strb r7,[r4,#5]
    ldr r7,=STOCK_DRAW
    blx r7
    movs r0,#0
    strb r0,[r4,#5]
    adds r6,#1
    strb r6,[r4,#16]
    cmp r6,#12
.ifdef G9_OLED_DIRTY_DRAW
    beq .Lredraw_done
    ldrh r1,[r4,#DELTA_DIRTY_MASK]
    cmp r1,#0
    beq .Ltick_done
    lsrs r1,r6
    bne .Ltick_done
.Lredraw_done:
    movs r0,#0
    strh r0,[r4,#DELTA_DIRTY_MASK]
.else
    bne .Ltick_done
.endif
    strb r0,[r4,#6]
    strb r0,[r4,#16]
.ifdef G9_EMBEDDED_OLED
    b .Ltick_done
.Llocal_restore:
    bl .Llocal_tick
.endif
.ifdef G9_OLED_DIRTY_DRAW
    b .Ltick_done
.Ldraw_paused:
    movs r0,#0
    strb r0,[r4,#16]
    strh r0,[r4,#DELTA_DIRTY_MASK]
.endif
.Ltick_done:
    add sp,#4
    pop {r4,r5,r6,r7,pc}
.size g9_ol9_restore_tick,.-g9_ol9_restore_tick
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

.equ OL9_CONTEXT, 0x00400f88
.equ OL9_RESPONSE, 0x00400f91
.equ OL9_BODY, 0x00400f93
.equ OL9_QUERY, 0x004011c8  /* Stock EP0 OUT staging: 0x40124D - 0x85. */
.equ OL9_STOCK_GET, 0x00033b85

.global g9_ol9_version_hook
.type g9_ol9_version_hook,%function
.thumb_func
g9_ol9_version_hook:
    strb r1, [r2]
    ldrb r0, [r0, #1]
    movs r3, #0x4f
    strb r3, [r2, #2]
    movs r3, #0x4c
    strb r3, [r2, #3]
.ifdef G9_OLED_RADIO_ACK
.ifdef G9_OLED_RADIO_RETRY
    movs r3, #0x45
.else
    movs r3, #0x44
.endif
.else
.ifdef G9_OLED_DIRTY_DRAW
    movs r3, #0x43
.else
.ifdef G9_OLED_DELTA_FAST_WAIT
    movs r3, #0x42
.else
.ifdef G9_OLED_DELTA
    movs r3, #0x41
.else
    movs r3, #0x39
.endif
.endif
.endif
.endif
    strb r3, [r2, #4]
    bx lr
.size g9_ol9_version_hook, .-g9_ol9_version_hook


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
.global g9_ol9_prepare_hook
.type g9_ol9_prepare_hook,%function
.thumb_func
g9_ol9_prepare_hook:
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
.size g9_ol9_prepare_hook, .-g9_ol9_prepare_hook

/* Hook 0x34308, replacing BL Get_Report_Data. Every22 request returns through
 * this handler; only the exact wired selector0/length64 request may be valid.
 * Stock A0 transport wake remains, but no extra wake/profile restore occurs.
 */
.global g9_ol9_get_hook
.type g9_ol9_get_hook,%function
.thumb_func
g9_ol9_get_hook:
    ldr r3, =OL9_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #0x22
    beq .Lstatus
    ldr r3, =OL9_STOCK_GET
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
    ldr r1, =OL9_BODY
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
    ldr r0, =OL9_QUERY
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
    ldr r4, =OL9_RESPONSE
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
.size g9_ol9_get_hook, .-g9_ol9_get_hook
.balign 4
.ltorg

/* Hook 0x37896, replacing STRB r6,[r0] / LDRB r2,[r5].
 * The stock EP0 OUT callback unconditionally marks mouse endpoint2 ready,
 * even when it is still transmitting its previous input report. Frequent
 * display chunks must not impersonate endpoint2's actual IN completion.
 * r1 already points to the received packet (SP+4, set at 0x37894).
 * Configuration SET4/5/6/8/12 and dedicated22 must not impersonate an IN
 * completion. Keymap8 reproduced the same busy-FIFO fault before any OLED.
 * Other report IDs retain the stock store; the real EP2 callback 0x387A0
 * remains untouched. This leaf preserves every original live register.
 */
.global g9_ol9_ep0_mouse_ready_hook
.type g9_ol9_ep0_mouse_ready_hook,%function
.thumb_func
g9_ol9_ep0_mouse_ready_hook:
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
.size g9_ol9_ep0_mouse_ready_hook, .-g9_ol9_ep0_mouse_ready_hook
.balign 4
.ltorg

/* RF-only SET callsite0x3012A. Normal SET04 remains native. Invalid stage0
 * or revision9 claims this handler before the stock setter. The OEM sensor
 * checksum is retained at50..51 BE; custom CRC52..53 LE also covers pixel54.
 * NEVER send this envelope to firmware without this handler. */
.global g9_ol9_radio_set_hook
.type g9_ol9_radio_set_hook,%function
.thumb_func
g9_ol9_radio_set_hook:
    push {r0, r1, r2, r3}
    ldr r3, =REQUEST_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #4
    bne .Lcarrier_legacy
    ldr r1, [r3, #4]
    ldr r0, =0x00400f91
    cmp r1, r0
    bne .Lcarrier_legacy
    movs r0, r1
    adds r0, #53
    ldrb r0, [r0]
    cmp r0, #9
    beq .Lcarrier_owned
    ldrb r0, [r1, #22]
    cmp r0, #0
    beq .Lcarrier_owned
.Lcarrier_legacy:
    ldr r3, =0x00033cd3
    mov r12, r3
    pop {r0, r1, r2, r3}
    bx r12
.Lcarrier_owned:
    add sp, #16
    push {r4, r5, r6, r7, lr}
    sub sp, #68
    ldrb r0, [r3, #3]
    cmp r0, #56
    bne .Lcarrier_bad_early
    ldrb r0, [r3, #8]
    cmp r0, #0
    bne .Lcarrier_bad_early
    ldr r0, =0x00400b5c
    ldrb r0, [r0]
    cmp r0, #9
    bne .Lcarrier_bad_early
    ldr r0, =0x00400ba0
    ldrb r2, [r0]
    cmp r2, #0
    bne .Lcarrier_bad_early
    ldrb r0, [r0, #1]
    cmp r0, #2
    bne .Lcarrier_bad_early
    mov r0, sp
    movs r2, #4
    strb r2, [r0]
    movs r2, #56
    strb r2, [r0, #1]
    adds r0, #2
    movs r2, #54
    ldr r3, =STOCK_MEMCPY
    blx r3
    mov r4, sp
    ldrb r0, [r4, #2]
    cmp r0, #1
    bne .Lcarrier_bad_early
    ldrb r0, [r4, #3]
    cmp r0, #0
    bne .Lcarrier_bad_early
    ldrb r0, [r4, #4]
    cmp r0, #1
    bne .Lcarrier_bad_early
    ldrb r0, [r4, #5]
    cmp r0, #0x3f
    bne .Lcarrier_bad_early
    ldrb r0, [r4, #9]
    cmp r0, #40
    bne .Lcarrier_bad_early
    ldrb r0, [r4, #24]
    cmp r0, #0
    bne .Lcarrier_bad_early
    movs r2, r4
    adds r2, #55
    ldrb r0, [r2]
    cmp r0, #9
    bne .Lcarrier_bad_early
    b .Lcarrier_checksums
.Lcarrier_bad_early:
    b .Lcarrier_bad
.Lcarrier_checksums:
    /* OEM SET04 checksum: unsigned sum of full packet3..49, BE50..51. */
    movs r0, r4
    adds r0, #3
    movs r1, #47
    movs r2, #0
.Lcarrier_sum:
    ldrb r3, [r0]
    adds r2, r2, r3
    adds r0, #1
    subs r1, #1
    bne .Lcarrier_sum
    ldrb r1, [r0]
    lsls r1, #8
    ldrb r3, [r0, #1]
    orrs r1, r3
    cmp r2, r1
    bne .Lcarrier_bad
    /* Save expected CRC, then append isolated pixel54 to bytes0..51.
     * .Lcrc16 resets its seed, so one contiguous53-byte invocation is used. */
    adds r0, #2
    ldrb r7, [r0]
    ldrb r1, [r0, #1]
    lsls r1, #8
    orrs r7, r1
    ldrb r1, [r0, #2]
    strb r1, [r0]
    movs r0, r4
    movs r1, #53
    bl .Lcrc16
    cmp r0, r7
    bne .Lcarrier_bad
    ldr r0, =0x0040102c
    ldrb r0, [r0]
    cmp r0, #0
    bne .Lcarrier_bad
    ldr r0, =0x00400b27
    ldrb r0, [r0]
    cmp r0, #0
    bne .Lcarrier_bad
    ldrb r5, [r4, #6]
    ldrb r6, [r4, #7]
    ldrb r7, [r4, #8]
    /* Overlap-safe reverse reconstruction: pixel54 ->51,25..49 ->26..50,
     * then10..23 ->12..25. Header writes occur only after every pixel moved. */
    movs r0, r4
    adds r0, #51
    ldrb r1, [r0, #3]
    strb r1, [r0]
    subs r0, #2
    movs r1, #25
.Lcarrier_pixels_high:
    ldrb r2, [r0]
    strb r2, [r0, #1]
    subs r0, #1
    subs r1, #1
    bne .Lcarrier_pixels_high
    subs r0, #1
    movs r1, #14
.Lcarrier_pixels_low:
    ldrb r2, [r0]
    strb r2, [r0, #2]
    subs r0, #1
    subs r1, #1
    bne .Lcarrier_pixels_low
.ifdef G9_OLED_DELTA
    cmp r5,#0xfe
    bne .Lcarrier_full_chunk
    /* This carrier owns no native profile selection. Clear the selector
     * before either publishing the delta or rejecting its payload. */
    ldr r1,=0x00400b9f
    movs r0,#0
    strb r0,[r1]
    orrs r6,r7
    bne .Lcarrier_bad
    movs r0,r4
    adds r0,#12
    bl .Lstore_delta_entry
    cmp r0,#2
    bne .Lcarrier_bad
    /* No initialize flag. A verified base already owns display state. */
    b .Lcommit_wake
.Lcarrier_full_chunk:
.endif
    movs r0, #0x22
    strb r0, [r4]
    movs r0, #64
    strb r0, [r4, #1]
    movs r0, #0x47
    strb r0, [r4, #2]
    movs r0, #0x4f
    strb r0, [r4, #3]
    movs r0, #4
    strb r0, [r4, #4]
    movs r0, #1
    strb r0, [r4, #5]
    movs r0, #0
    strb r0, [r4, #6]
    strb r5, [r4, #7]
    strb r6, [r4, #8]
    movs r1, #40
    strb r1, [r4, #9]
    strb r7, [r4, #10]
    strb r0, [r4, #11]
    movs r1, r4
    adds r1, #52
    movs r2, #10
.Lcarrier_zero:
    strb r0, [r1]
    adds r1, #1
    subs r2, #1
    bne .Lcarrier_zero
    ldr r1, =0x00400b9f
    strb r0, [r1]
    movs r0, r4
    movs r1, #62
    bl .Lcrc16
    strh r0, [r4, #62]
    b .Lvalidate
.Lcarrier_bad:
    b .Ldone
.size g9_ol9_radio_set_hook, .-g9_ol9_radio_set_hook
.balign 4
.ltorg

/* RF GET remains the stock dispatcher; this prototype adds no RF status API. */
.global g9_ol9_radio_get_hook
.type g9_ol9_radio_get_hook,%function
.thumb_func
g9_ol9_radio_get_hook:
    ldr r3, =OL9_STOCK_GET
    bx r3
.size g9_ol9_radio_get_hook, .-g9_ol9_radio_get_hook
.balign 4
.ltorg

/* Discard direct custom22 RF prefixes before reassembly/profile selection.
 * Other prefix formats keep their native behavior. The carrier client must
 * use exactly zero kind/selector and active profile1; it is not implemented. */
.global g9_ol9_radio_prefix_hook
.type g9_ol9_radio_prefix_hook,%function
.thumb_func
g9_ol9_radio_prefix_hook:
    push {r4, lr}
    ldr r3, =0x000373ad
    blx r3
    ldr r0, =0x00400eef
    ldrb r1, [r0, #1]
    cmp r1, #0x22
    bne .Lprefix_done
    ldrb r1, [r0]
    cmp r1, #0x36
    beq .Lprefix_custom
    cmp r1, #0x37
    bne .Lprefix_done
.Lprefix_custom:
    b .Lprefix_bad /* Direct report22 is not a qualified receiver bridge. */
.Lprefix_bad:
    movs r1, #0
    strb r1, [r0]          /* Stock unknown-prefix discard, no new state. */
.Lprefix_done:
    pop {r4, pc}
.size g9_ol9_radio_prefix_hook, .-g9_ol9_radio_prefix_hook
.balign 4
.ltorg

.ifdef G9_EMBEDDED_OLED
.include "firmware/g9_embedded_oled.S"
.endif
.ifdef G9_OLED_DELTA
.include "firmware/g9_oled_delta.S"
.endif

.ifdef G9_OLED_RADIO_ACK
/* Hook 0x35EBC replaces CMP r0,#0 / BEQ 0x35F64. Stock notifications
 * and all five ordinary-input predicates have priority over the probe. */
.global g9_radio_ack_idle_hook
.type g9_radio_ack_idle_hook,%function
.thumb_func
g9_radio_ack_idle_hook:
    cmp r0,#0
    bne .Lack_input
    bl .Llookup
    cmp r0,#0
    beq .Lack_idle
    ldr r1,=META
    adds r3,r0,r1
    ldrb r0,[r3,#1]       /* Valid committed frame. */
    ldrb r1,[r3,#4]       /* Host owns the display. */
    ands r0,r1
    ldrb r1,[r3,#19]      /* Controller initialized. */
    ands r0,r1
    cmp r0,#1
    bne .Lack_idle
    ldrb r0,[r3,#6]       /* Renderer still has work. */
    ldrb r1,[r3,#LOCAL_OWNER]
    orrs r0,r1
    ldr r1,=0x00400a7a   /* Asleep. */
    ldrb r1,[r1]
    orrs r0,r1
    ldr r1,=0x00400af2   /* User disabled the display. */
    ldrb r1,[r1]
    orrs r0,r1
    bne .Lack_idle
    ldr r0,[r3,#8]
    ldr r1,[r3,#RADIO_ACK_GENERATION]
    cmp r0,r1
    beq .Lack_idle
    str r0,[r3,#RADIO_ACK_GENERATION]
    ldrh r2,[r3,#12]     /* Existing CRC-16 of the entire drawn frame. */
    movs r0,#0x35
    strb r0,[r4]
    movs r0,#0xc9
    strb r0,[r4,#1]
    movs r0,#0xd1
    strb r0,[r4,#2]
    strb r2,[r4,#3]
    lsrs r2,#8
    strb r2,[r4,#4]
    adds r1,r4,#5
    movs r0,#0
    movs r2,#27
.Lack_clear_tail:
    strb r0,[r1]
    adds r1,#1
    subs r2,#1
    bne .Lack_clear_tail
    ldr r0,=0x00035dff   /* Set the native queue-ready flag and return. */
    bx r0
.Lack_idle:
    ldr r0,=0x00035f65
    bx r0
.Lack_input:
    ldr r0,=0x00035ec1
    bx r0
.size g9_radio_ack_idle_hook,.-g9_radio_ack_idle_hook
.balign 4
.ltorg
.endif
