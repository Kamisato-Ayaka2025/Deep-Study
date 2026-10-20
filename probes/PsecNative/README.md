# PSEC 1.1 developer probe

This fixed synthetic experiment is not the production Runner or a P1 acceptance substitute. It runs on the current Home machine using `processmodel.dll`. It does not install Windows Sandbox, change shared toolchain ACLs, or configure the firewall. Run in an ordinary interactive user shell; the tool execution sandbox can prevent isolation APIs.

From the repository root:

```powershell
./scripts/probe-p1-psec.ps1 -Toolchain mingw -PythonPath <python.exe>
./scripts/probe-p1-psec.ps1 -Toolchain msvc -PythonPath <python.exe>
```

The existing MinGW installation builds the trusted native launcher; the selected compiler builds the attack fixture **inside PSEC**. Python is a developer-only dependency. Install `flatbuffers==25.12.19` into `.tools/psec-python` after dot-sourcing `scripts/environment.ps1`; the driver does not install it automatically. MSVC discovery uses installed `vswhere` and trusted `vcvars64.bat`.

`spec.py` encodes the public [Microsoft PSEC schema](https://github.com/microsoft/mxc/blob/6f6e43683877d68a43fdd1d6c201808c914c5685/external/windows-sdk/ProcessSecurityEnvironment.fbs). The policy denies mounted drive roots, grants work read/write and discovered compiler/system dependencies read-only, and has default-deny networking with no allow rules or network capabilities. Only `registryRead,lpacCom` remain. The user approved removing local networking from the initial product scope; see ADR-005.

`escape.c` extends the reviewed synthetic escape fixture to derive the actual PSEC profile, probe a child's ambient-file read, and read host-verified aliases. All file targets are synthetic. Socket listeners are on this machine. DNS uses the existing `example.com` positive control.

The driver exits nonzero if the tested subset fails. JSON always says `fullP1Acceptance=false`. External IPv4 tests make fixed DNS requests for example.com to the configured public service 223.5.5.5:53, with host controls before and after. Local TCP/UDP listeners must remain untouched; internal loopback must fail. IPv6 is explicitly excluded by the user on this host, remains unverified, and is not probed (including mapped addresses). The legacy report field tcpV6Error=-9 means unexecuted, never passed.

The launcher sets 1 GiB process memory, 2 GiB job memory, 16 active processes and 25% CPU hard cap. A fixed fixture verifies rejection of a commitment exceeding the process cap and the sixteenth child (the parent occupies one slot). Stop and launcher-crash tests pin process handles and verify both parent and child exit. CPU rate enforcement under load, aggregate job memory, disk/output capacity, production host cleanup, and malicious host-file processing still require validation. Both pipe paths identify one synthetic pipe, not the actual Runner control channel. Short names, symbolic links, production integration and model-egress gates remain pending. Do not approve P1 or enable learner programs using this result.

Evidence in `artifacts/review-P1-20261015/psec/matrix-*` includes exact source, encoder, binary policy, commands, logs, reports and summary. Earlier failures remain evidence; do not overwrite them.
