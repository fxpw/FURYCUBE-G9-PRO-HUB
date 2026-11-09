/* OL3 OFFLINE CANDIDATE. Includes the byte-identical OL2 page uploader.
 * New status source: configured runtime DPI, exactly as stock OLED 0x3D328.
 * No firmware installation, persistent RAM allocation or NVM operations here.
 * Compile with -I firmware; existing g9_oled.S / payload remain unchanged.
 */
.include "g9_oled.S"

.equ OL3_CONTEXT, 0x00400f88
.equ OL3_RESPONSE, 0x00400f91
.equ OL3_BODY, 0x00400f93
.equ OL3_QUERY, 0x004011c8  /* Stock EP0 OUT staging: 0x40124D - 0x85. */
.equ OL3_STOCK_GET, 0x00033b85

.global g9_ol3_version_hook
.type g9_ol3_version_hook,%function
.thumb_func
g9_ol3_version_hook:
    strb r1, [r2]
    ldrb r0, [r0, #1]
    movs r3, #0x4f
    strb r3, [r2, #2]
    movs r3, #0x4c
    strb r3, [r2, #3]
    movs r3, #0x33
    strb r3, [r2, #4]
    bx lr
.size g9_ol3_version_hook, .-g9_ol3_version_hook

/* Hook 0x342A4, replacing STRB r2,[r0,#11] / BEQ 0x342B2.
 * All report22 reads, including malformed ones, skip selector-based preload
 * and the normal clearing of macro/profile selectors. Other IDs replay both
 * paths exactly. r0=context,r2=selector,r3=body,r4=FSM,r6=0.
 */
.global g9_ol3_prepare_hook
.type g9_ol3_prepare_hook,%function
.thumb_func
g9_ol3_prepare_hook:
    strb r2, [r0, #11]
    push {r1}
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
.size g9_ol3_prepare_hook, .-g9_ol3_prepare_hook

/* Hook 0x34308, replacing BL Get_Report_Data. Every22 request returns through
 * this handler; only the exact wired selector0/length132 request may be valid.
 * Stock A0 transport wake remains, but no extra wake/profile restore occurs.
 */
.global g9_ol3_get_hook
.type g9_ol3_get_hook,%function
.thumb_func
g9_ol3_get_hook:
    ldr r3, =OL3_CONTEXT
    ldrb r0, [r3, #2]
    cmp r0, #0x22
    beq .Lstatus
    ldr r3, =OL3_STOCK_GET
    bx r3
.Lstatus:
    push {r4, r5, r6, r7, lr}
    movs r7, #0             /* Invalid unless the exact request is proved. */
    ldrb r0, [r3, #1]
    cmp r0, #2
    bne .Lclear
    ldrb r0, [r3, #10]
    cmp r0, #132
    bne .Lclear
    ldrb r0, [r3, #11]
    cmp r0, #0
    bne .Lclear
    ldr r0, [r3, #4]
    ldr r1, =OL3_BODY
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
    ldr r0, =OL3_QUERY
    ldrb r1, [r0]
    cmp r1, #0xa0
    bne .Lclear
    ldrb r1, [r0, #1]
    cmp r1, #0x22
    bne .Lclear
    ldrb r1, [r0, #2]
    cmp r1, #132
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
    ldr r4, =OL3_RESPONSE
    movs r0, #0
    movs r1, #132
    movs r2, r4
.Lzero:
    strb r0, [r2]
    adds r2, #1
    subs r1, #1
    bne .Lzero
    movs r0, #0x22
    strb r0, [r4]
    movs r0, #132
    strb r0, [r4, #1]
    movs r0, #0x47
    strb r0, [r4, #2]
    movs r0, #0x53
    strb r0, [r4, #3]
    movs r0, #3
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
    movs r1, #130
    bl .Lcrc16             /* Existing OL2 CRC routine, no added ROM calls. */
    adds r4, #130
    strb r0, [r4]
    lsrs r0, #8
    strb r0, [r4, #1]
    pop {r4, r5, r6, r7, pc}
.size g9_ol3_get_hook, .-g9_ol3_get_hook
.balign 4
.ltorg
