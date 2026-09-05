# Discovery fixtures

These are synthetic payloads created for the discovery-only milestone, not
captures from the user's radio or evidence of firmware compatibility.

- `p1-hermes-lite.hex`: 63 bytes, P1 discovery status 2, locally administered MAC
  `02-00-00-00-00-01`, board ID 6, code version 73 and receiver count 2.
- `p2-saturn.hex`: 60 bytes, P2 discovery status 2, locally administered MAC
  `02-00-00-00-00-02`, board ID 10 (Saturn), protocol field 2, code version 42,
  receiver count 10 and beta field 3.

Field offsets follow the existing parser at SDR-VST3 `3518930b`. Tests mutate
copies to exercise busy status, P1 model mappings, malformed lengths, invalid
headers/MACs and protocol/subnet/target filtering. The socket seam injects
duplicates, quiet polls, errors, cancellation and continuous unrelated traffic.
No ordinary unit test opens a network socket or contacts a radio.

These fixtures test preservation of the existing parser; independent simulator
and real-radio comparisons remain necessary to validate protocol compatibility.
