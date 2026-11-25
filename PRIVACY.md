# Privacy Policy — Project R Agent

**Effective Date:** 2026-11-25

Project R Agent is an open-source client agent for self-hosted **Project R ITAM & MDM** servers.

## 1. Data Collected Locally
When enrolled by a device administrator or user, the agent collects system and device inventory information required for IT asset management and device policy compliance, such as:
- Hardware identifiers (hostname, device ID, UUID, serial number, CPU, RAM, storage, display, battery, TPM/BitLocker status)
- Operating system version, installed software inventory, and network configuration (IP, MAC, Wi-Fi SSID/BSSID)
- Policy compliance evaluations and location data only if explicitly enabled by the organization's self-hosted server configuration

## 2. Where Data Is Sent
Project R Agent **does not send telemetry, analytics, or personal data to the project authors or any third-party cloud**.
All collected data is transmitted **exclusively to the self-hosted Project R server URL** configured during installation or enrollment by the organization/administrator operating that server.

## 3. Code Signing Notice
Official Windows binaries published in GitHub Releases use free code signing provided by [SignPath.io](https://signpath.io), with certificates issued by the [SignPath Foundation](https://signpath.org).

## 4. Contact
For questions or security reports regarding the open-source agent, please open an issue at:
https://github.com/riadysandi/Project-R-Agent/issues
