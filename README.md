<p align="center">
  <a href="https://interstellar.rip"><img src="Resources/GitHub/brand.svg" width="128" height="128" alt="Interstellar"></a>
</p>

<h1 align="center">Interstellar Loader</h1>

<p align="center">Install and manage Interstellar Menu for Gorilla Tag.</p>

<p align="center"><strong>v1.0.1</strong></p>

<p align="center">
  <a href="https://github.com/fleecewtf/Interstellar-Loader/releases/latest"><strong>Download Loader</strong></a>
  &nbsp;&nbsp;
  <a href="https://github.com/fleecewtf/Interstellar-Loader/releases">Releases</a>
  &nbsp;&nbsp;
  <a href="https://github.com/fleecewtf/Interstellar">Interstellar Menu</a>
  &nbsp;&nbsp;
  <a href="https://interstellar.rip">Website</a>
  &nbsp;&nbsp;
  <a href="https://discord.gg/fZJQ5PaBm6">Discord</a>
</p>

---

<details open>
<summary><strong>How do I use the loader?</strong></summary>

1. Download **Interstellar Loader.exe** from the [latest release](https://github.com/fleecewtf/Interstellar-Loader/releases/latest).
2. Open it and check the detected Gorilla Tag folder. You can change the folder in **Settings**.
3. Close Gorilla Tag, then use the launcher to set up BepInEx or install Interstellar if needed.
4. Launch the game with **Launch with Interstellar** enabled.

The loader supports the **Windows x64 Steam version of Gorilla Tag** and **BepInEx 5 Mono**. It requires **.NET Framework 4.8**.

</details>

<details>
<summary><strong>Do I need to move the menu DLL myself?</strong></summary>

No. The loader downloads the latest published Interstellar Menu release and installs it into your game's **BepInEx/plugins** folder. It also includes an approved menu build for use when no online release is available. You can reinstall it through **Manage → Install / repair menu**.

If you previously installed Interstellar manually, close the game and remove the old Interstellar DLL before installing through the loader.

Close Gorilla Tag before installing, repairing or removing files.

</details>

<details>
<summary><strong>What if I don't have BepInEx or Gorilla Tag installed?</strong></summary>

Open **Manage** for BepInEx installation, repair and removal. BepInEx setup downloads the official package.

The game installation and repair buttons open Steam's own installation or file-validation process.

</details>

<details>
<summary><strong>Can I load Interstellar while the game is running?</strong></summary>

Yes, when the installed Interstellar component is already running and connected to the loader. Install Interstellar before starting the game to make that connection available.

If the launcher says the game is running without a connection, close Gorilla Tag, install the menu, and launch again.

</details>

<details>
<summary><strong>How do I update or remove it?</strong></summary>

The loader checks both Interstellar GitHub release feeds every **15 seconds**, including while it is in the tray. Enable **Settings → Auto-update menu** and **Auto-update loader** to install new releases automatically.

Menu updates wait until Gorilla Tag is closed and preserve your startup preference. Loader updates show download progress, restart the loader, and restore the previous loader if the new build cannot start. Downloads are checked against GitHub's SHA-256 digest and the expected Interstellar assembly identity and version.

Turning either automatic update option off still lets you check for new releases. You can also download the latest EXE from [Releases](https://github.com/fleecewtf/Interstellar-Loader/releases) and replace the old loader manually, or use **Install / repair menu** to install the latest menu release.

Restoring a previous menu build turns off automatic menu updates so the restored build stays installed. You can enable them again in Settings. Offline or rate-limited checks retry without removing your installed files.

To remove the menu, close Gorilla Tag and choose **Manage → Remove Interstellar**. To remove the loader, exit it fully and delete its EXE.

</details>

<details>
<summary><strong>Does closing the window exit the loader?</strong></summary>

That depends on **Settings → Close to tray**. Enable it to keep the loader in your system tray, or disable it to exit when you close the window. The tray's **Exit** option always closes it fully.

Opening the loader again brings the existing instance forward.

</details>

<details>
<summary><strong>Why is the loader open source?</strong></summary>

I don't like hiding vibecoded code behind obfuscation. Interstellar is open source so you can inspect what the loader does on your system and check the code for yourself. I want people to feel comfortable using it with the source available to review.

</details>

<details>
<summary><strong>Where can I report a bug?</strong></summary>

Join the [Interstellar Discord](https://discord.gg/fZJQ5PaBm6) and use the bug-report channels or open a support ticket. Include your loader version, what happened, and the steps that caused it. Screenshots or relevant error messages help when available.

</details>

<details>
<summary><strong>Can I use the code?</strong></summary>

Yes. The loader code is available under [GPL-3.0-or-later](LICENSE). See the license for its permissions and terms.

Interface icons retain their third-party licenses. Required notices are included in [Assets](Assets).

</details>

---

<details open>
<summary><strong>Support Interstellar</strong></summary>

If you'd like to support Interstellar, you can donate to one of these addresses.

| Network | Address |
| --- | --- |
| Litecoin mainnet | `LVhzGNeWeztmUd8KtwVwEna9xGR56yx9mY` |
| Bitcoin mainnet | `bc1q9deqkuam636f43ekd3eam25paxltaw2up5dwu8` |
| Ethereum mainnet | `0x907101339b33FDb8b13cBDfB893B0266e6533dDd` |

</details>
