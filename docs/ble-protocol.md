# BLE transport protocol v1

This is the unauthenticated stage-2 transport, not the final authorization protocol. Neither executable submits Windows credentials. Keep the phone service enabled only during development; any nearby client may issue test requests. HMAC/key pairing remains a separate stage.

## GATT

| Item | UUID | Properties |
| --- | --- | --- |
| Service | `6d87f6f0-3274-4f00-8e0a-770450f8a900` | Advertised, primary |
| Challenge | `6d87f6f0-3274-4f00-8e0a-770450f8a901` | Write with response |
| Approval | `6d87f6f0-3274-4f00-8e0a-770450f8a902` | Indicate |
| Status | `6d87f6f0-3274-4f00-8e0a-770450f8a903` | Read/write |

Approval uses CCCD `00002902-0000-1000-8000-00805f9b34fb` with `ENABLE_INDICATION_VALUE` (`02 00`). Subscribe before sending a challenge. The phone accepts one connected client at a time. Status reads return UTF-8 `transport-v1`; writing UTF-8 `cancel` cancels that client's pending request.

## Framing

Challenges and results are UTF-8 JSON followed by a single byte LF (`0a`). JSON strings must escape any line breaks. Each GATT write/indication carries at most 20 bytes, including the final LF; this fits the minimum ATT MTU of 23. Assemble bytes before decoding UTF-8 so multibyte characters can cross chunk boundaries.

The maximum frame is 4096 bytes excluding LF. Empty, oversized, malformed, or unsupported challenge frames are rejected and the connection is closed. The phone clears incomplete challenge bytes after a 5-second gap between writes. Prepared writes and nonzero offsets are unsupported. Result indications are serialized and acknowledged; a stalled result transmission closes the connection after 5 seconds.

## Messages

```json
{
  "v": 1,
  "type": "unlock_request",
  "request_id": "12345678-1234-4321-8321-123456789abc",
  "nonce": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
  "expires_in": 10,
  "computer_name": "My PC"
}
```

`request_id` is a canonical lowercase UUID, `nonce` is canonical Base64 encoding of 32 cryptographically random bytes, and `expires_in` is an integer from 5 to 120. `computer_name` is a nonempty display label of at most 64 characters without control characters. The all-zero example nonce above is illustrative; Windows generates a fresh random nonce for every request.

```json
{
  "v": 1,
  "type": "approval",
  "request_id": "12345678-1234-4321-8321-123456789abc",
  "nonce": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
  "decision": "approved"
}
```

Decisions are `approved`, `rejected`, `expired`, `cancelled`, `replaced`, `busy`, and `unavailable`. No response contains the reply phrase. Only `approved` confirms a matching reply, and even that is not Windows login authorization.

## Request lifetime

Windows begins its monotonic deadline before the first challenge write. Android begins its own deadline after validating the complete message, using the shorter of the challenge and phone lifetimes. Delayed transport therefore never extends the Windows deadline. The phone checks replies against the pending request and deadline; Windows additionally checks its outstanding UUID and nonce. Completed requests are single-use. Unknown results are ignored or rejected; phone reboot and transport loss cannot produce confirmation.

Only one request may be active. A busy phone may return `busy`; a request received while an existing result frame is being sent is ignored to avoid interleaving result frames. Duplicate UUIDs are ignored using a bounded 128-entry cache while the service runs. This cache is not persistent anti-replay authentication and must not replace the planned HMAC protocol. After a failure, explicitly create a new request rather than retrying an old challenge.
