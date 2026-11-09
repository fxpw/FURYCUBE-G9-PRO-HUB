/* G9 PRO 1.08: stateless, wired-only OLED page uploader, protocol OL2.
 * Offline candidate source. No persistent RAM allocation or flash writes.
 * Compile: clang --target=arm-none-eabi -march=armv5t -mthumb -c g9_oled.S
 * Hooks and framing are applied by a separate, exact-image candidate builder.
 */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4

.equ REQUEST_CONTEXT, 0x00400f88
.equ WIRED_PAYLOAD,   0x00400f93
.equ STOCK_WAKE,      0x00034af9
.equ STOCK_MEMCPY,    0x00024f9d
.equ STOCK_OLED_INIT, 0x0003cddf
.equ STOCK_DRAW,      0x0003cee7

/* Replace four bytes at 0x33c94 with BL g9_oled_version_hook.
 * Replay STRB r1,[r2] / LDRB r0,[r0,#1], then add marker to the three
 * zero padding bytes of the existing, declared 8-byte report 0x0b.
 * Stock instruction at 0x33c98 still writes the minor version afterwards.
 */
.global g9_oled_version_hook
.type g9_oled_version_hook,%function
.thumb_func
g9_oled_version_hook:
    strb r1, [r2]
    ldrb r0, [r0, #1]
    movs r3, #0x4f
    strb r3, [r2, #2]
    movs r3, #0x4c
    strb r3, [r2, #3]
    movs r3, #0x32
    strb r3, [r2, #4]
    bx lr
.size g9_oled_version_hook, .-g9_oled_version_hook

/* Replace the BL STOCK_WAKE at 0x33cd4 with BL g9_oled_set_hook.
 * All other reports tail-call the exact original function. Report 0x22
 * proceeds only with the known wired USB payload pointer and a 132-byte
 * transfer. The outer stock dispatcher subsequently takes its default return.
 */
.global g9_oled_set_hook
.type g9_oled_set_hook,%function
.thumb_func
g9_oled_set_hook:
    ldr r3, =REQUEST_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #0x22
    beq .Lcustom
    ldr r3, =STOCK_WAKE
    bx r3
.Lcustom:
    push {r4, r5, r6, r7, lr}
    sub sp, #132
    ldrb r0, [r3, #3]
    cmp r0, #132
    bne .Ldone
    ldr r1, [r3, #4]
    ldr r0, =WIRED_PAYLOAD
    cmp r1, r0
    bne .Ldone
    subs r1, #2
    mov r0, sp
    movs r2, #132
    ldr r3, =STOCK_MEMCPY
    blx r3
    mov r4, sp
    ldrb r0, [r4]
    cmp r0, #0x22
    bne .Ldone
    ldrb r0, [r4, #1]
    cmp r0, #132
    bne .Ldone
    ldrb r0, [r4, #2]
    cmp r0, #0x47
    bne .Ldone
    ldrb r0, [r4, #3]
    cmp r0, #0x4f
    bne .Ldone
    ldrb r0, [r4, #4]
    cmp r0, #2
    bne .Ldone
    ldrb r0, [r4, #5]
    cmp r0, #1
    bne .Ldone
    ldrb r0, [r4, #6]
    cmp r0, #0
    bne .Ldone
    ldrb r5, [r4, #7]
    cmp r5, #3
    bhi .Ldone
    ldrb r0, [r4, #8]
    cmp r0, #120
    bne .Ldone
    ldrb r6, [r4, #9]
    cmp r6, #1
    bhi .Ldone
    movs r0, r4
    movs r1, #130
    bl .Lcrc16
    movs r1, r4
    adds r1, #130
    ldrb r2, [r1]
    ldrb r1, [r1, #1]
    lsls r1, r1, #8
    orrs r1, r2
    cmp r0, r1
    bne .Ldone
    cmp r5, #3
    bne .Lvalid
    movs r0, r4
    adds r0, #10
    movs r1, #120
    movs r3, #0xf0
.Llast_page:
    ldrb r2, [r0]
    tst r2, r3
    bne .Ldone
    adds r0, #1
    subs r1, #1
    bne .Llast_page
.Lvalid:
    /* Only now may hardware be touched. No new frame/ownership state exists. */
    ldr r3, =STOCK_WAKE
    blx r3
    cmp r6, #0
    beq .Ldraw
    ldr r3, =STOCK_OLED_INIT
    blx r3
.Ldraw:
    movs r0, #0
    movs r1, r5
    movs r2, #120
    movs r3, r4
    adds r3, #10
    ldr r7, =STOCK_DRAW
    blx r7
.Ldone:
    add sp, #132
    pop {r4, r5, r6, r7, pc}
.size g9_oled_set_hook, .-g9_oled_set_hook

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
