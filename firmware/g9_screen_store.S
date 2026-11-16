/* Isolated offline retention store. NOT integrated into OL4 or installed.
 * Requires allocator-end literal4306C:406F2C ->406F1C, reserving16B.
 * Boot hook replaces28D58:ldr r0,=40006D / movs r5,0; call once after
 * BSS initialization, before platform setup, with startup IRQ/FIQ masked.
 * Main context exclusively owns staging/commit/restore. No NVM operations.
 */
.syntax unified
.arch armv5t
.thumb
.text
.balign 4
.equ ROOT, 0x00406f1c
.equ MAGIC, 0x32524f47 /* GOR2: prefix-protected buffers plus retained CRC. */
.equ MALLOC, 0x00043005
.equ FIRST_ALLOCATION, 0x00404f34
.equ META, 964

.global g9_store_boot_hook
.type g9_store_boot_hook,%function
.thumb_func
g9_store_boot_hook:
    push {r1,r2,r3,r4,r6,r7,lr}
    sub sp,#4
    ldr r4,=ROOT
    movs r6,#0
    ldr r0,[r4]
    ldr r1,=MAGIC
    cmp r0,r1
    bne .Lreset_root
    ldr r0,[r4,#4]
    ldr r1,=FIRST_ALLOCATION
    cmp r0,r1
    bne .Lreset_root
    bl .Lvalidate_frame
    movs r6,r0 /* Valid retained state before allocator's lazy reset. */
.Lreset_root:
    movs r0,#0
    str r0,[r4]
    str r0,[r4,#4]
    str r0,[r4,#8]
    str r0,[r4,#12]
    movs r0,#1
    str r0,[r4,#8] /* Allocation attempted exactly once this boot. */
    lsls r0,#10
    ldr r3,=MALLOC
    blx r3
    cmp r0,#0
    beq .Lboot_done
    str r0,[r4,#4]
    cmp r6,#1
    bne .Lfresh_meta
    ldr r1,=FIRST_ALLOCATION
    cmp r0,r1
    bne .Lfresh_meta
    bl .Lvalidate_frame
    cmp r0,#1
    bne .Lfresh_meta_reload
    ldr r0,[r4,#4]
    ldr r1,=META
    adds r1,r0,r1
    movs r0,#0
    strb r0,[r1,#2] /* Discard any interrupted staging transaction. */
    strb r0,[r1,#3]
    strb r0,[r1,#5] /* Renderer cannot remain active through reset. */
    strb r0,[r1,#16]
    strb r0,[r1,#19] /* A surviving frame does not prove controller init. */
    ldrb r0,[r1,#4]
    strb r0,[r1,#6] /* Custom owner requests a complete main-loop restore. */
    b .Lpublish
.Lfresh_meta_reload:
    ldr r0,[r4,#4]
.Lfresh_meta:
    ldr r1,=META
    adds r1,r0,r1
    movs r2,#0
    movs r3,#60
.Lzero_meta:
    strb r2,[r1]
    adds r1,#1
    subs r3,#1
    bne .Lzero_meta
.Lpublish:
    ldr r0,=MAGIC
    str r0,[r4] /* Publish only after metadata is initialized. */
.Lboot_done:
    ldr r0,=0x0040006d
    cmp r5,r5 /* Stock reset's final BSS CMP leavesC=1,V=0. */
    movs r5,#0
    add sp,#4
    pop {r1,r2,r3,r4,r6,r7,pc}
.size g9_store_boot_hook,.-g9_store_boot_hook
.balign 4
.ltorg

/* r0=index0..11,r1=validated40-byte pixels. Returns0=rejected,
 * 1=staged,2=committed. Index0 always begins a new staging transaction.
 * Outer transport must validate packet size/header/CRC before calling.
 */
.global g9_store_chunk
.type g9_store_chunk,%function
.thumb_func
g9_store_chunk:
.Lstore_chunk_entry:
    push {r3,r4,r5,r6,r7,lr}
    movs r4,r0
    movs r5,r1
    bl .Llookup
    cmp r0,#0
    beq .Lreject_return
    movs r6,r0
    ldr r7,=META
    adds r7,r6,r7
    cmp r4,#11
    bhi .Labort
    cmp r4,#0
    bne .Lcheck_order
    movs r0,#1
    strb r0,[r7,#2]
    movs r0,#0
    strb r0,[r7,#3]
.Lcheck_order:
    ldrb r0,[r7,#2]
    cmp r0,#1
    bne .Labort
    ldrb r0,[r7,#3]
    cmp r0,r4
    bne .Labort
    cmp r4,#9
    blo .Lcopy
    movs r0,r5
    movs r1,#40
    movs r3,#0xf0
.Lnibble:
    ldrb r2,[r0]
    tst r2,r3
    bne .Labort
    adds r0,#1
    subs r1,#1
    bne .Lnibble
.Lcopy:
    ldrb r0,[r7]
    movs r1,#1
    eors r0,r1
    movs r1,#240
    lsls r1,#1
    muls r0,r1
    adds r0,r6,r0
    adds r0,#4 /* Lazy allocator owns the first4 bytes while rebuilding. */
    movs r1,#40
    muls r1,r4
    adds r0,r0,r1
    movs r1,#40
.Lcopy_byte:
    ldrb r2,[r5]
    strb r2,[r0]
    adds r5,#1
    adds r0,#1
    subs r1,#1
    bne .Lcopy_byte
    adds r4,#1
    strb r4,[r7,#3]
    cmp r4,#12
    bne .Lstaged
    movs r0,r6
    adds r0,#4
    ldrb r1,[r7]
    cmp r1,#0
    bne .Lcrc_staged
    movs r1,#240
    lsls r1,#1
    adds r0,r0,r1
.Lcrc_staged:
    bl .Lframe_crc
    strh r0,[r7,#12]
    ldrb r0,[r7]
    movs r1,#1
    eors r0,r1
    strb r0,[r7]
    strb r1,[r7,#1] /* valid */
    strb r1,[r7,#4] /* custom owner */
    strb r1,[r7,#6] /* restore pending */
    movs r0,#0
    strb r0,[r7,#2]
    strb r0,[r7,#16]
    ldr r0,[r7,#8]
    adds r0,#1
    str r0,[r7,#8]
    movs r0,#2
    b .Lchunk_return
.Lstaged:
    movs r0,#1
    b .Lchunk_return
.Labort:
    movs r0,#0
    strb r0,[r7,#2]
    strb r0,[r7,#3]
.Lreject_return:
    movs r0,#0
.Lchunk_return:
    pop {r3,r4,r5,r6,r7,pc}
.size g9_store_chunk,.-g9_store_chunk
.balign 4
.ltorg

/* Returns retained valid frame pointer, independent of native/custom owner. */
.global g9_store_active
.type g9_store_active,%function
.thumb_func
g9_store_active:
    push {r4,lr}
    bl .Llookup
    cmp r0,#0
    beq .Lactive_done
    ldr r1,=META
    adds r1,r0,r1
    ldrb r2,[r1,#1]
    cmp r2,#1
    bne .Lactive_none
    ldrb r2,[r1]
    movs r1,#240
    lsls r1,#1
    muls r2,r1
    adds r0,r0,r2
    adds r0,#4
    b .Lactive_done
.Lactive_none:
    movs r0,#0
.Lactive_done:
    pop {r4,pc}
.size g9_store_active,.-g9_store_active
.balign 4
.ltorg

/* Internal root validation. r0=allocation or0; clobbersr1..r3 only. */
.thumb_func
.Llookup:
    ldr r1,=ROOT
    ldr r2,[r1]
    ldr r3,=MAGIC
    cmp r2,r3
    bne .Lno_root
    ldr r0,[r1,#4]
    bx lr
.Lno_root:
    movs r0,#0
    bx lr
.balign 4
.ltorg

/* r0=known allocated block. Validate before publishing or dereferencing any
 * retained bank selector; only callers that checked exact pointer use this.
 * r0=1 valid,0 invalid. Preservesr4..r7. */
.thumb_func
.Lvalidate_frame:
    push {r4,r5,r6,lr}
    movs r4,r0
    ldr r1,=META
    adds r5,r0,r1
    ldrb r0,[r5]
    cmp r0,#1
    bhi .Linvalid_frame
    ldrb r1,[r5,#1]
    cmp r1,#1
    bne .Linvalid_frame
    ldrb r1,[r5,#4]
    cmp r1,#1
    bhi .Linvalid_frame
    movs r1,#240
    lsls r1,#1
    muls r0,r1
    adds r0,r4,r0
    adds r0,#4
    bl .Lframe_crc
    ldrh r1,[r5,#12]
    cmp r0,r1
    bne .Linvalid_frame
    movs r0,#1
    b .Lvalidate_done
.Linvalid_frame:
    movs r0,#0
.Lvalidate_done:
    pop {r4,r5,r6,pc}
.balign 4
.ltorg

/* CRC16/CCITT-FALSE over480 pixels; no ROM calls or frame writes. */
.thumb_func
.Lframe_crc:
    push {r4,r5,r6,r7,lr}
    movs r4,r0
    ldr r5,=0xffff
    ldr r6,=0x1021
    movs r7,#240
    lsls r7,#1
.Lcrc_byte:
    ldrb r0,[r4]
    adds r4,#1
    lsls r0,#8
    eors r5,r0
    movs r1,#8
.Lcrc_bit:
    lsls r2,r5,#16
    bpl .Lcrc_plain
    lsls r5,#1
    eors r5,r6
    b .Lcrc_truncate
.Lcrc_plain:
    lsls r5,#1
.Lcrc_truncate:
    lsls r5,#16
    lsrs r5,#16
    subs r1,#1
    bne .Lcrc_bit
    subs r7,#1
    bne .Lcrc_byte
    movs r0,r5
    pop {r4,r5,r6,r7,pc}
.balign 4
.ltorg
