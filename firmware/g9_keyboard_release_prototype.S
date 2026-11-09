/* OFFLINE PROTOTYPE, NOT AN INSTALLABLE CANDIDATE.
 * Explicit policy: replacing/loading a button table ends this mouse's virtual
 * keyboard keys. No-wait / OL2 / OL3 source and published hashes are unchanged.
 * No new RAM, boot/flash calls, macro cancellation, or keyboard event queue.
 *
 * Hooks are calls AFTER the stock table copies:
 *  0x30D80 BL 0x374A8 -> profile_load_hook (original logger is BX LR).
 *  0x33F4E BL 0x24F9C -> button_copy_hook (replays ROM memcpy).
 * The old caller continuation, original callee effects, r4-r7 and the stack
 * are preserved. The helper additionally preserves r0-r4 after that callee.
 * Assembly is position independent except for documented existing RAM/ROM.
 * Compile: clang --target=arm-none-eabi -march=armv5t -mthumb -c THIS.S
 */
.syntax unified
.cpu arm926ej-s
.thumb
.text
.p2align 2

.global g9_release_profile_load_hook
.type g9_release_profile_load_hook,%function
.thumb_func
g9_release_profile_load_hook:
    push {r4, lr}
    ldr r4, =0x000374a9
    blx r4                       /* Exact stock log callee (BX LR). */
    bl .Lrelease_keyboard
    pop {r4, pc}
.size g9_release_profile_load_hook, .-g9_release_profile_load_hook

.global g9_release_button_copy_hook
.type g9_release_button_copy_hook,%function
.thumb_func
g9_release_button_copy_hook:
    push {r4, lr}
    ldr r4, =0x00024f9d
    blx r4                       /* Exact original ROM memcpy arguments. */
    bl .Lrelease_keyboard
    pop {r4, pc}
.size g9_release_button_copy_hook, .-g9_release_button_copy_hook

.global g9_release_keyboard
.type g9_release_keyboard,%function
.thumb_func
g9_release_keyboard:
.Lrelease_keyboard:
    push {r0, r1, r2, r3, r4, lr}
    movs r2, #0                  /* OR of previous buffer/FSM/dirty state. */
    movs r3, #0
    movs r4, #8
    ldr r0, =0x00400b42          /* Stock keyboard reset uses these 8 bytes. */
.Lclear_buffer:
    ldrb r1, [r0]
    orrs r2, r1
    strb r3, [r0]
    adds r0, #1
    subs r4, #1
    bne .Lclear_buffer
    ldr r0, =0x00400b2b
    ldrb r1, [r0]
    orrs r2, r1
    strb r3, [r0]                /* Cancel pending ordinary key sequence. */
    ldrb r1, [r0, #1]
    orrs r2, r1
    strb r3, [r0, #1]            /* Cancel pending shortcut sequence. */
    ldr r0, =0x00400af6
    ldrb r1, [r0]
    orrs r2, r1
    cmp r2, #0
    beq .Ldone                   /* Idle loads do not invent empty reports. */
    movs r1, #1
    strb r1, [r0]                /* Empty keyboard report remains pending. */
    ldr r0, =0x00400b19
    strb r1, [r0]
    strb r1, [r0, #1]            /* Existing RF / USB scheduler request bits. */
.Ldone:
    pop {r0, r1, r2, r3, r4, pc}
.size g9_release_keyboard, .-g9_release_keyboard
.ltorg
