/* Optional offline OLED snapshot. r4=store metadata, r5=store allocation.
 * Use the same 40-byte draw primitive as OL9 and draw at most once per tick.
 * The compiled base frame owns bank 0. Overlay pixels live only on the stack.
 */
.global g9_embedded_startup_hook
.type g9_embedded_startup_hook,%function
.thumb_func
g9_embedded_startup_hook:
    /* The splash and its delay have finished. Claim frame ownership before
     * the following native clear and status redraw can write live pixels. */
    push {r4,r5,r6,lr}
    bl .Llookup
    cmp r0,#0
    beq .Llocal_startup_clear
    movs r5,r0
    ldr r1,=META
    adds r4,r0,r1
    ldrb r0,[r4,#1]
    cmp r0,#0
    bne .Llocal_startup_clear
    ldrb r0,[r4,#19]
    cmp r0,#1
    bne .Llocal_startup_clear
    bl .Llocal_seed
.Llocal_startup_clear:
    ldr r3,=0x0003d825
    blx r3
    pop {r4,r5,r6,pc}
.size g9_embedded_startup_hook,.-g9_embedded_startup_hook
.balign 4
.ltorg

.thumb_func
.Llocal_tick:
    push {r4,r5,r6,r7,lr}
    sub sp,#44
    ldr r0,=0x00400a7a
    ldrb r0,[r0]
    cmp r0,#0
    bne .Llocal_return
    ldr r0,=0x00400af2
    ldrb r0,[r0]
    cmp r0,#0
    bne .Llocal_return
    ldrb r0,[r4,#19]
    cmp r0,#1
    bne .Llocal_return
    ldrb r0,[r4,#1]
    cmp r0,#1
    beq .Llocal_have_base
    bl .Llocal_seed
.Llocal_have_base:
    ldrb r6,[r4,#16]
    ldrb r0,[r4,#6]
    cmp r0,#0
    bne .Llocal_scheduled
    bl .Llocal_level
    ldrb r1,[r4,#LOCAL_DRAWN_LEVEL]
    cmp r0,r1
    beq .Llocal_return
    strb r0,[r4,#LOCAL_LEVEL]
    movs r0,#2
    strb r0,[r4,#6]
.Llocal_scheduled:
    cmp r0,#2
    beq .Llocal_partial
    cmp r6,#0
    bne .Llocal_prepare
    bl .Llocal_level
    strb r0,[r4,#LOCAL_LEVEL]
    b .Llocal_prepare
.Llocal_partial:
    adr r0,.Llocal_header
    adds r0,#20 /* first index, after 8-byte header and 12-byte map */
    ldrb r6,[r0,r6]
.Llocal_prepare:
    movs r3,#40
    muls r3,r6
    adds r3,r5,r3
    adds r3,#4
    adr r1,.Llocal_header
    movs r0,r1
    adds r0,#8
    ldrb r0,[r0,r6]
    cmp r0,#0
    beq .Llocal_draw

    /* Map entries are one-based. Each record holds 40 thresholds and
     * 40 XOR masks, so layer overlap and inversion require no MCU layout code. */
    subs r0,#1
    movs r2,#80
    muls r0,r2
    ldrh r2,[r1,#6]
    adds r1,r1,r2
    adds r1,r1,r0
    movs r0,r3
    ldrb r2,[r4,#LOCAL_LEVEL]
    mov r3,sp
    bl .Llocal_mix
    mov r3,sp
.Llocal_draw:
    movs r0,r6
    movs r1,#0
.Llocal_page:
    cmp r0,#3
    blo .Llocal_page_ready
    subs r0,#3
    adds r1,#1
    b .Llocal_page
.Llocal_page_ready:
    movs r2,#40
    muls r0,r2
    movs r7,#1
    strb r7,[r4,#5]
    ldr r7,=STOCK_DRAW
    blx r7
    movs r0,#0
    strb r0,[r4,#5]
    ldrb r1,[r4,#16]
    adds r1,#1
    strb r1,[r4,#16]
    movs r2,#12
    ldrb r3,[r4,#6]
    cmp r3,#2
    bne .Llocal_check_end
    adr r2,.Llocal_header
    ldrb r2,[r2]
.Llocal_check_end:
    cmp r1,r2
    bne .Llocal_return
    strb r0,[r4,#16]
    strb r0,[r4,#6]
    ldrb r0,[r4,#LOCAL_LEVEL]
    strb r0,[r4,#LOCAL_DRAWN_LEVEL]
.Llocal_return:
    add sp,#44
    pop {r4,r5,r6,r7,pc}

.thumb_func
.Llocal_seed:
    push {r6,lr}
    adr r6,.Llocal_header
    ldrh r1,[r6,#4]
    adds r1,r6,r1
    adds r0,r5,#4
    ldr r2,=480
    ldr r3,=STOCK_MEMCPY
    blx r3
    ldrh r0,[r6,#2]
    strh r0,[r4,#12]
    movs r0,#0
    strb r0,[r4]
    strb r0,[r4,#16]
    movs r0,#1
    strb r0,[r4,#1]
    strb r0,[r4,#4]
    strb r0,[r4,#6]
    strb r0,[r4,#LOCAL_OWNER]
    pop {r6,pc}

.thumb_func
.Llocal_level:
    ldr r0,=0x00400ac6 /* Stock battery formatter uses 100 minus this byte. */
    ldrb r0,[r0]
    cmp r0,#100
    bhi .Llocal_old_level
    rsbs r0,r0,#0
    adds r0,#100
    movs r1,#0
.Llocal_divide:
    cmp r0,#10
    blo .Llocal_level_done
    subs r0,#10
    adds r1,#1
    b .Llocal_divide
.Llocal_level_done:
    movs r0,r1
    bx lr
.Llocal_old_level:
    ldrb r0,[r4,#LOCAL_LEVEL]
    bx lr

.thumb_func
.Llocal_mix:
    push {r4,r5,r6,r7,lr}
    movs r4,#40
    movs r5,r1
    adds r5,#40
.Llocal_mix_byte:
    ldrb r6,[r0]
    ldrb r7,[r1]
    cmp r7,r2
    bhi .Llocal_store_byte
    ldrb r7,[r5]
    eors r6,r7
.Llocal_store_byte:
    strb r6,[r3]
    adds r0,#1
    adds r1,#1
    adds r3,#1
    adds r5,#1
    subs r4,#1
    bne .Llocal_mix_byte
    pop {r4,r5,r6,r7,pc}

.balign 4
.ltorg
.include "g9_embedded_snapshot.inc"
