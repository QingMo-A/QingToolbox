# PowerGuard 0.1.0 Deployment Acceptance

This is a manual release-candidate checklist. Record each item as **Pass**, **Fail**, **Blocked**, or **Not tested**. Automated CI does not complete these real-environment checks.

## A. Safety preparation

- [ ] Use a virtual machine or dedicated test computer. Result: ______
- [ ] Save all work; do not perform the first test on a production computer. Result: ______
- [ ] Confirm available UPS runtime even though PowerGuard does not read UPS status. Result: ______
- [ ] Confirm QingToolbox starts at Windows login. Result: ______
- [ ] Confirm the PowerGuard card has **Start with toolbox** enabled. Result: ______

## B. Safe preview

- [ ] Open the test warning; confirm it never requests shutdown. Result: ______
- [ ] Verify Chinese and English text. Result: ______
- [ ] Verify cancel and ten-minute extension controls. Result: ______
- [ ] Verify DPI scaling and lower-left placement on each intended display. Result: ______

## C. Network scenarios

- [ ] Disable the network adapter. Result: ______
- [ ] Unplug Ethernet. Result: ______
- [ ] Power off the test router. Result: ______
- [ ] Verify one target failing does not alone confirm an outage. Result: ______
- [ ] Verify a brief outage does not start shutdown. Result: ______
- [ ] Verify stable recovery confirmation. Result: ______
- [ ] Cancel one continuous outage and confirm it stays suppressed. Result: ______
- [ ] Restore connectivity, disconnect again, and confirm protection rearms. Result: ______

## D. Normal shutdown (test computer only)

- [ ] Shorten settings while remaining inside the UI's legal ranges. Result: ______
- [ ] Confirm the final connectivity probe occurs. Result: ______
- [ ] Confirm the fixed command is `shutdown.exe /s /t 0`. Result: ______
- [ ] Confirm `/f` is absent and the request occurs only once. Result: ______
- [ ] Record how applications with unsaved work block or delay shutdown. Result: ______

## E. Long-running operation

- [ ] Run continuously for 24 hours. Result: ______
- [ ] Exercise sleep and resume; confirm a fresh grace period. Result: ______
- [ ] Exercise Windows automatic clock synchronization. Result: ______
- [ ] Exercise normal network fluctuations. Result: ______
- [ ] Verify toolbox floating-widget behavior. Result: ______
- [ ] Close the module view and confirm monitoring continues. Result: ______
- [ ] Verify the 1 MiB event-log rotation. Result: ______
- [ ] Observe memory and handle counts for sustained growth. Result: ______

Tester: ______  Build/commit: ______  Date: ______  Overall result: ______
