# Authenticated BLE protocol v2

Implemented in Android and Windows client 0.4.0. Both programs must use v2; v1 is rejected. This protocol authenticates notification requests and decisions. Neither program submits Windows credentials.

## GATT and framing

| Item | UUID | Properties |
| --- | --- | --- |
| Service | `6d87f6f0-3274-4f00-8e0a-770450f8a900` | Advertised, primary |
| Challenge | `6d87f6f0-3274-4f00-8e0a-770450f8a901` | Write with response |
| Approval | `6d87f6f0-3274-4f00-8e0a-770450f8a902` | Indicate |
| Status | `6d87f6f0-3274-4f00-8e0a-770450f8a903` | Read only |

Status reads return UTF-8 `paired-v2`, a capability marker rather than a claim that the current connection has paired. Subscribe to Approval using CCCD `00002902-0000-1000-8000-00805f9b34fb` and `ENABLE_INDICATION_VALUE` (`02 00`) before pairing. The phone accepts one connected client. Disconnection or subscription cancellation invalidates the session and pending BLE request. An unpaired connection is released after 60 seconds.

Every message is a flat UTF-8 JSON object followed by LF (`0a`). JSON strings escape line breaks. Each write/indication carries at most 20 bytes, including LF, fitting ATT MTU 23. Assemble the entire frame before decoding UTF-8. Frames have at most 4096 bytes excluding LF. Duplicate keys, nested values, invalid field types, unsupported versions, and trailing data are rejected. Malformed or unauthenticated input cannot grant approval; Android closes the connection on invalid requests.

Android clears incomplete incoming bytes after a 5-second gap between writes. Prepared writes and nonzero offsets are unsupported. Outgoing indications are serialized and acknowledged; a stalled indication closes the connection after 5 seconds. There is no plaintext `cancel` command; cancel on the phone, stop BLE, or disconnect.

## Shared Key and canonical authentication

Enter the same Key on both devices: 16–128 printable ASCII characters in range 33–126, with no spaces. Use a random Key distinct from the confirmation phrase and Windows credentials. Derive the 32-byte long-term key with PBKDF2-HMAC-SHA256, 210000 iterations, and UTF-8 salt `WatchUnlock pairing v2`. Restricting the input to ASCII gives identical password bytes on both platforms.

Android imports the derived HMAC key into Android Keystore (`watchunlock-pairing-v2`); preferences retain only a random configuration version. Windows holds the derived key only in process memory and clears it on exit or replacement. The input Key, derived long-term key and session key are never sent over BLE. Changing the Android Key stops BLE and cancels a pending BLE request.

`MAC(domain, fields...)` means Base64 encoding of HMAC-SHA256 over these UTF-8 bytes:

```text
watchunlock-v2
<domain>
<field1>
<field2>
...
```

Use LF separators and **no trailing LF** in the MAC input. JSON field order and whitespace do not affect the MAC. UUIDs are canonical lowercase; nonces and MACs use canonical padded Base64 of 32 bytes. TTL is invariant decimal. Computer names are Base64 of UTF-8 before inclusion in canonical bytes. MAC comparisons use constant-time byte comparison.

## Mutual pairing

All messages include integer `v: 2`. The table lists the remaining exact fields and MAC input, in order. `id` is a fresh UUID; `cn` and `sn` are fresh 32-byte client and server nonces.

| Type | Other fields | MAC input |
| --- | --- | --- |
| `pair_request` | `request_id=id`, `client_nonce=cn`, `mac` | `pair_request, id, cn` |
| `pair_challenge` | `request_id=id`, `client_nonce=cn`, `server_nonce=sn`, `mac` | `pair_challenge, id, cn, sn` |
| `pair_confirm` | `request_id=id`, `client_nonce=cn`, `server_nonce=sn`, `mac` | `pair_confirm, id, cn, sn` |
| `pair_ok` | `request_id=id`, `client_nonce=cn`, `server_nonce=sn`, `mac` | `pair_ok, id, cn, sn` |

Each pairing MAC uses the long-term key. Windows verifies both phone proofs and all ID/nonce bindings before reporting success. Android verifies the client request and confirmation. Android's confirmation deadline is 30 seconds from accepting `pair_request`; Windows limits the full handshake to 35 seconds, within a 60-second overall connection budget.

After confirmation, `session_id = id`. The session key is the **raw** 32-byte HMAC of `session_key, id, cn, sn` using the long-term key. It is derived locally on both sides. A recorded confirmation cannot complete a new handshake with a different server nonce.

Android may send `{"v":2,"type":"pair_error","reason":"key_mismatch"}` or reason `expired`, then close the connection. This error is unauthenticated and can only fail pairing. It cannot authorize a request. Pairing proves possession of the Key, not a Bluetooth address or a persistent device identity; any device holding that Key can pair. GATT payloads are not encrypted by this application protocol, and OS-level Bluetooth bonding is not required. HMAC provides authenticity, not confidentiality or protection against a live relay.

## Signed request and result

```json
{
  "v": 2,
  "type": "unlock_request",
  "session_id": "12345678-1234-4321-8321-123456789abc",
  "request_id": "22345678-1234-4321-8321-123456789abc",
  "nonce": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
  "expires_in": 10,
  "computer_name": "My PC",
  "mac": "<Base64 HMAC>"
}
```

The all-zero nonce is illustrative; Windows generates fresh random bytes. `expires_in` is an integer from 5 to 120. `computer_name` is a nonblank display label of at most 64 UTF-16 code units without control characters. Its request MAC uses the session key and canonical fields `unlock_request, session_id, request_id, nonce, expires_in, Base64(UTF8(computer_name))`. Android requires the current session and validates the MAC before creating a notification.

```json
{
  "v": 2,
  "type": "approval",
  "session_id": "12345678-1234-4321-8321-123456789abc",
  "request_id": "22345678-1234-4321-8321-123456789abc",
  "nonce": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
  "decision": "approved",
  "mac": "<Base64 HMAC>"
}
```

Its MAC uses `approval, session_id, request_id, nonce, decision` with the session key. Decisions are `approved`, `rejected`, `expired`, `cancelled`, `replaced`, `busy`, and `unavailable`. No result includes the confirmation phrase. Only `approved` confirms a matching human reply within the request lifetime; account authentication remains a later Windows integration.

## Lifetimes and replay rejection

Windows starts a monotonic deadline before the first request write. Android starts its deadline after validating the complete request, using the shorter of the requested and configured lifetimes. Delayed transport never extends the Windows deadline. Windows verifies the result MAC, current session, outstanding UUID/nonce and its own deadline. Terminal replies are single-use; stale or expired results cannot complete a new request.

Only one request is pending. A busy phone returns `busy` when its outgoing queue is idle, otherwise ignores the extra request to avoid a result backlog. Duplicate request UUIDs are ignored using a bounded 128-entry cache for the connection; Windows' outstanding request checks and fresh session keys remain authoritative for accepting results. Disconnection, service termination, Key changes and phone reboot invalidate active requests. After failure, create a fresh request and connection rather than resending an old frame.
