# Restore only the hardware profile pair

`G9Device profile-restore --file <settings snapshot.json> --pid 2148` restores the saved **active profile / available profile count** pair. It exists so an invalid full body in the currently selected profile does not prevent returning to the saved profile. It does not restore profile bodies, assignments, macros or firmware, and is not a firmware-recovery or flash-backup feature.

The input must be a version-1 verified `FirmwareLogicalProfile` settings snapshot with numeric wired product ID 8520 (`0x2148`), `isFirmwareBackup=false`, `macroBodiesIncluded=false`, and the exact opened device path, compared case-insensitively. Legacy string product IDs are rejected. The saved `rawReports.profile` must be exactly eight hexadecimal bytes, with report/length/selector `0C 08 01`, valid active/count complements, zero final padding, and `1 <= active <= count <= 5`. The JSON active/count/profile metadata must agree with those bytes; `profileBefore`, when present, must agree too. Duplicate JSON members, including equivalent Unicode-escaped names, are rejected before interpreting the file.

No current full-body read is required. After input validation the transaction:

1. Reads the current pair using GET 0x0C and validates its complements, padding and bounds.
2. Saves the current raw pair, saved source raw pair, and source snapshot path/SHA256/profile metadata to a new local archive. Each file is closed after `Flush(true)` before any settings SET.
3. Reads GET 0x0C again and requires the same current pair and device identity.
4. Sends exactly one existing validated SET 0x0C restoring the saved pair.
5. Reads GET 0x0C once more and requires exact agreement with the saved raw report.

Only step 5 permits `PROFILE_RESTORE_JSON` with `verified=true`, `status=restored-profile-pair`, and `verificationScope=profile-pair-only`. The result links the newly created pair archive. An archive/type/path/product/complement/guard/filesystem failure before SET sends no settings write. A failure after SET returns an error, without a success result, resend, reset or automatic rollback. Device profile selection and normal query handling have their usual firmware side effects; no claim is made that unrelated physical RAM or flash is independently verified by this narrow operation.

The pair archive itself is labeled `HardwareProfilePairBeforeRestore`, not as a complete settings snapshot or firmware backup. It records the previous pair and intended source for review. The command accepts the original logical-settings snapshot type, not an arbitrary pair archive.

Pure validation in `SettingsTransactionTests` covers an invalid current full body, known exact SET bytes, durable archive ordering, same-path case folding, malformed/ambiguous/foreign snapshots, legacy product types, saved/current complement and ordering failures, profile/device drift, filesystem/transport failures, and missing success results after a failed post-write verification. **245 settings transaction checks pass** with synthetic transport. The audit agent did not run this command on a physical device.
