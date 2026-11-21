/* One radio command carries a patch against an already committed host frame.
 * r0=validated carrier's 40-byte delta. Only the inactive bank is scratched
 * before all descriptors, the visible tail, and the target CRC are checked.
 */
.balign 4
.global g9_store_delta
.type g9_store_delta,%function
.thumb_func
g9_store_delta:
.Lstore_delta_entry:
    push {r4,r5,r6,r7,lr}
.ifdef G9_OLED_RADIO_RETRY
    sub sp,#28
    str r0,[sp,#20]                /* Original canonical delta payload. */
.else
    sub sp,#20
.endif
    movs r4,r0
    bl .Llookup
    cmp r0,#0
    beq .Ldelta_bad_early
    ldr r7,=META
    adds r7,r0,r7
    ldrb r1,[r7,#1]
    cmp r1,#1
    bne .Ldelta_bad_early
    ldrb r1,[r7,#4]
    cmp r1,#1
    bne .Ldelta_bad_early
    ldrb r1,[r7,#2]
    cmp r1,#0
    bne .Ldelta_bad_early
.ifdef G9_EMBEDDED_OLED
    ldrb r1,[r7,#LOCAL_OWNER]
    cmp r1,#0
    bne .Ldelta_bad_early
.endif
    ldrb r1,[r7]
    cmp r1,#1
    bhi .Ldelta_bad_early
    adds r0,#4
    ldr r2,=480
    movs r6,r0
    movs r5,r0
    cmp r1,#0
    beq .Ldelta_inactive_high
    adds r5,r5,r2
    b .Ldelta_bank_ready
.Ldelta_inactive_high:
    adds r6,r6,r2
.Ldelta_bank_ready:
    mov r0,sp
    movs r1,r4
    movs r2,#8
    ldr r3,=STOCK_MEMCPY
    blx r3                         /* Align incoming CRC fields. */
    movs r0,r5
    bl .Ldelta_crc32
    ldr r1,[sp,#0]
    cmp r0,r1
.ifdef G9_OLED_RADIO_RETRY
    beq .Ldelta_base_matches
    ldr r1,[sp,#4]
    cmp r0,r1
    bne .Ldelta_bad_early
    bl .Ldelta_retry_ack
    b .Ldelta_bad_early
.Ldelta_base_matches:
.else
    bne .Ldelta_bad_early
.endif
    ldrb r0,[r4,#8]
    cmp r0,#1
    blo .Ldelta_bad_early
    cmp r0,#10
    bhi .Ldelta_bad_early
    str r0,[sp,#16]
    movs r0,r4
    adds r0,#40
    str r0,[sp,#8]
    movs r0,#0
    str r0,[sp,#12]                 /* End of previous run. */
    adds r4,#9
    movs r0,r6
    movs r1,r5
    ldr r2,=480
    ldr r3,=STOCK_MEMCPY
    blx r3
    b .Ldelta_run
.Ldelta_bad_early:
    b .Ldelta_bad
.Ldelta_run:
    ldr r3,[sp,#8]
    subs r3,r3,r4
    cmp r3,#3
    blo .Ldelta_bad                 /* Descriptor plus at least one byte. */
    ldrb r0,[r4]
    ldrb r1,[r4,#1]
    lsls r1,#8
    orrs r0,r1
    adds r4,#2
    subs r3,#2
    lsrs r2,r0,#9
    adds r2,#1                      /* Length. */
    cmp r2,r3
    bhi .Ldelta_bad
    lsls r0,#23
    lsrs r0,#23                     /* Absolute byte offset. */
    ldr r1,[sp,#12]
    cmp r0,r1
    blo .Ldelta_bad
    adds r1,r0,r2
    ldr r3,=480
    cmp r1,r3
    bhi .Ldelta_bad
    str r1,[sp,#12]
    adds r0,r6,r0
    movs r1,r4
    adds r4,r4,r2
    ldr r3,=STOCK_MEMCPY
    blx r3
    ldr r0,[sp,#16]
    subs r0,#1
    str r0,[sp,#16]
    bne .Ldelta_run
    ldr r1,[sp,#8]
.Ldelta_padding:
    cmp r4,r1
    beq .Ldelta_visible_tail
    ldrb r0,[r4]
    cmp r0,#0
    bne .Ldelta_bad
    adds r4,#1
    b .Ldelta_padding
.Ldelta_visible_tail:
    ldr r0,=360
    adds r0,r6,r0
    movs r1,#120
    movs r2,#0xf0
.Ldelta_nibbles:
    ldrb r3,[r0]
    tst r3,r2
    bne .Ldelta_bad
    adds r0,#1
    subs r1,#1
    bne .Ldelta_nibbles
    movs r0,r6
    bl .Ldelta_crc32
    ldr r1,[sp,#4]
    cmp r0,r1
    bne .Ldelta_bad
.ifdef G9_OLED_DIRTY_DRAW
    movs r4,#0
    ldrb r0,[r7,#6]
    cmp r0,#0
    bne .Ldelta_publish
    ldrb r0,[r7,#19]
    cmp r0,#1
    bne .Ldelta_publish
    ldr r0,=0x00400a7a
    ldrb r0,[r0]
    ldr r1,=0x00400af2
    ldrb r1,[r1]
    orrs r0,r1
    bne .Ldelta_publish
    movs r0,r5
    movs r1,r6
    bl .Ldelta_dirty_chunks
    movs r4,r0
.Ldelta_publish:
.endif
    movs r0,#0
    strb r0,[r7,#17]
    movs r0,r6
    bl .Lframe_crc
    g9_publish_frame
.ifdef G9_OLED_RADIO_RETRY
    ldr r0,[sp,#20]
    movs r1,#40
    bl .Ldelta_crc32_length
    str r0,[r7,#RADIO_ACK_DELTA_SIGNATURE]
    ldr r0,[r7,#8]
    str r0,[r7,#RADIO_ACK_DELTA_GENERATION]
    movs r0,#2
.endif
.ifdef G9_OLED_DIRTY_DRAW
    strh r4,[r7,#DELTA_DIRTY_MASK]
.endif
    b .Ldelta_done
.Ldelta_bad:
    movs r0,#0
.Ldelta_done:
.ifdef G9_OLED_RADIO_RETRY
    add sp,#28
.else
    add sp,#20
.endif
    pop {r4,r5,r6,r7,pc}
.size g9_store_delta,.-g9_store_delta
.balign 4
.ltorg

.ifdef G9_OLED_RADIO_RETRY
/* The caller checked the active target CRC32. Match the packet to the
 * latest verified delta before making its receipt eligible again. The
 * normal idle drain still waits for all OLED writes and yields to input. */
.thumb_func
.Ldelta_retry_ack:
    push {r3,lr}
    ldr r0,[r7,#8]
    ldr r1,[r7,#RADIO_ACK_DELTA_GENERATION]
    cmp r0,r1
    bne .Ldelta_retry_done
    movs r0,r4
    movs r1,#40
    bl .Ldelta_crc32_length
    ldr r1,[r7,#RADIO_ACK_DELTA_SIGNATURE]
    cmp r0,r1
    bne .Ldelta_retry_done
    ldr r0,[r7,#8]
    subs r0,#1
    str r0,[r7,#RADIO_ACK_GENERATION]
.Ldelta_retry_done:
    pop {r3,pc}
.endif

.ifdef G9_OLED_DIRTY_DRAW
/* r0/r1: aligned old/new banks. Compare ten words per 40-byte chunk.
 * Once any word differs, skip to the next chunk. Return a 12-bit mask. */
.thumb_func
.Ldelta_dirty_chunks:
    push {r4,r5,r6,lr}
    movs r4,#0
    movs r5,#1
.Ldelta_compare_chunk:
    movs r2,#10
.Ldelta_compare_word:
    ldmia r0!,{r3}
    ldmia r1!,{r6}
    subs r2,#1
    cmp r3,r6
    bne .Ldelta_chunk_differs
    cmp r2,#0
    bne .Ldelta_compare_word
    b .Ldelta_next_chunk
.Ldelta_chunk_differs:
    orrs r4,r5
    lsls r2,#2
    adds r0,r0,r2
    adds r1,r1,r2
.Ldelta_next_chunk:
    lsls r5,#1
    lsrs r2,r5,#12
    beq .Ldelta_compare_chunk
    movs r0,r4
    pop {r4,r5,r6,pc}
.balign 4
.endif

.ifdef G9_OLED_DELTA_FAST_WAIT
/* Replace the native BL to memcpy at 0x300DE. At this point the stock
 * handler has accepted the sequence byte and is about to copy 30 body
 * bytes. The first eight identify the delta; the trailing CRC is only
 * available after body 2 and is still checked by the normal SET hook. */
.equ DELTA_RF_WAIT_TICKS, 64
.global g9_delta_first_body_hook
.type g9_delta_first_body_hook,%function
.thumb_func
g9_delta_first_body_hook:
    push {r0,r1,r2,r3}
    ldr r3,=0x00400f91
    cmp r0,r3
    bne .Ldelta_copy
    cmp r2,#30
    bne .Ldelta_copy
    ldr r3,=REQUEST_CONTEXT
    ldrh r0,[r3,#2]
    ldr r2,=0x3804
    cmp r0,r2
    bne .Ldelta_copy
    ldrb r0,[r3,#8]
    cmp r0,#0
    bne .Ldelta_copy
    ldr r3,=0x00400b5c
    ldrb r0,[r3]
    cmp r0,#9
    bne .Ldelta_copy
    ldr r3,=0x00400ba1
    ldrb r0,[r3]
    cmp r0,#1
    bne .Ldelta_copy
    ldrb r0,[r1,#0]
    cmp r0,#1
    bne .Ldelta_copy
    ldrb r0,[r1,#1]
    cmp r0,#0
    bne .Ldelta_copy
    ldrb r0,[r1,#2]
    cmp r0,#1
    bne .Ldelta_copy
    ldrb r0,[r1,#3]
    cmp r0,#0x3f
    bne .Ldelta_copy
    ldrb r0,[r1,#4]
    cmp r0,#0xfe
    bne .Ldelta_copy
    ldrb r0,[r1,#5]
    ldrb r2,[r1,#6]
    orrs r0,r2
    ldrb r2,[r1,#22]
    orrs r0,r2
    bne .Ldelta_copy
    ldrb r0,[r1,#7]
    cmp r0,#40
    bne .Ldelta_copy
    ldr r3,=0x00400b62
    ldrb r0,[r3]
    cmp r0,#DELTA_RF_WAIT_TICKS
    bls .Ldelta_copy
    movs r0,#DELTA_RF_WAIT_TICKS
    strb r0,[r3]
.Ldelta_copy:
    ldr r3,=STOCK_MEMCPY
    mov r12,r3
    pop {r0,r1,r2,r3}
    bx r12
.size g9_delta_first_body_hook,.-g9_delta_first_body_hook
.balign 4
.ltorg
.endif

/* CRC32/ISO-HDLC over exactly 480 bytes. A 16-entry table keeps this
 * smaller than a byte table and avoids eight per-byte bit branches. */
.thumb_func
.Ldelta_crc32:
.ifdef G9_OLED_RADIO_RETRY
    ldr r1,=480
.Ldelta_crc32_length:
.endif
    push {r4,r5,r6,lr}
    movs r4,r0
    movs r0,#0
    mvns r0,r0
.ifdef G9_OLED_RADIO_RETRY
    movs r5,r1
.else
    ldr r5,=480
.endif
    adr r6,.Ldelta_crc_table
.Ldelta_crc_byte:
    ldrb r1,[r4]
    adds r4,#1
    eors r0,r1
    lsls r1,r0,#28
    lsrs r1,#26
    ldr r1,[r6,r1]
    lsrs r0,#4
    eors r0,r1
    lsls r1,r0,#28
    lsrs r1,#26
    ldr r1,[r6,r1]
    lsrs r0,#4
    eors r0,r1
    subs r5,#1
    bne .Ldelta_crc_byte
    mvns r0,r0
    pop {r4,r5,r6,pc}
.balign 4
.Ldelta_crc_table:
    .word 0x00000000,0x1db71064,0x3b6e20c8,0x26d930ac
    .word 0x76dc4190,0x6b6b51f4,0x4db26158,0x5005713c
    .word 0xedb88320,0xf00f9344,0xd6d6a3e8,0xcb61b38c
    .word 0x9b64c2b0,0x86d3d2d4,0xa00ae278,0xbdbdf21c
.ltorg
