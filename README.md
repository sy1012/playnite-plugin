<!-- trunk-ignore-all(markdownlint/MD033) -->
<!-- trunk-ignore(markdownlint/MD041) -->
<div align="center">

  <img src=".github/resources/isotipo.png" height="180px" width="auto" alt="romm-muos logo">
    <h3 style="font-size: 25px;">
    A beautiful, powerful, RomM plugin for Playnite.
  </h3>

<br>

[![license-badge-img]][license-badge]
[![release-badge-img]][release-badge]
[![discord-badge-img]][discord-badge]

<!-- [![wiki-badge-img]][wiki] -->

  </div>
</div>

# Overview

This plugin allows you to import your RomM library into Playnite. It queries the RomM API to create Playnite library entries for each of your games. Installing a game in Playnite will download it from RomM and store it on your system, allowing you to launch it in your emulator of choice.

> [!WARNING]
> The plugin requires a RomM instance set up with IGDB API credentials, otherwise it won't work. You can find more information on how to generate and set credentials in the [docs](https://docs.romm.app/latest/Getting-Started/Generate-API-Keys/).

# Save sync

With save sync on, a game's save is downloaded from RomM before it launches and uploaded after you
play. Saves are filed in RomM by game, the `autosave` slot and file name, and laid out the way
[Tender](https://github.com/sy1012/romm-tender) (Steam Deck) and Argosy lay them, so the same save
moves between those clients and this one.

| Emulator | Save | Uploaded as |
| --- | --- | --- |
| RetroArch | the core's battery save | `<rom>.srm` |
| RetroArch, Dolphin core — GameCube | the game's `.gci` files in the GCI folder of slot A (`dolphin-emu/User/GC/<region>/Card A`) | `<rom>.zip`, flat |
| RetroArch, Dolphin core — Wii | `dolphin-emu/User/Wii/title/<high>/<low>` | `<rom>.zip`, under `<low>/` |
| Eden | `nand/user/save/0000000000000000/<current user>/<application id>` | `<rom>.zip`, under `<application id>/` |
| Cemu | `<mlc>/usr/save/<high>/<low>` | `<rom>.zip`, under `<low>/` |
| RPCS3 | every `dev_hdd0/home/<user>/savedata` folder starting with the title id | `<rom>.zip`, each folder at the top |

The id a save is keyed by is read from the game file: a Switch `[01…]` file-name tag or NSP ticket, a
Wii U `.wua` or a dump's `code/app.xml`, a PS3 folder dump's `PARAM.SFO`, `.pkg` header or ISO, and a
GameCube/Wii disc header (ISO, GCM, CISO, GCZ, WBFS, WIA, RVZ). A game whose id cannot be read is
skipped and the reason logged. RPCS3 needs exactly one user account; Eden needs a user profile; a
GameCube raw memory card is shared by every game and is not synced.

Before a downloaded save replaces a save of several files, the plugin keeps a zip of the one it
replaces in its data folder under `SaveBackups\<game>` (the last 10 per game).

# Installation

To install and configure the RomM Playnite plugin, check out the [Installation Guide][docs-installation-guide]

# Contributing

To contribute to RomM Playnite plugin, please check [Contribution Guide](./CONTRIBUTING.md)

For setting up a local development environment (building, deploying, and debugging the plugin), see the [Development Guide](./DEVELOPMENT.md).

# Community

Join us on Discord, where you can ask questions, submit ideas, get help, showcase your collection, and discuss RomM with other users.

[![discord-invite]][discord-invite-url]

# Technical Support

If you have any issues with the plugin, please [open an issue](https://github.com/rommapp/playnite-plugin/issues/new) in this repository. If the issue is with RomM itself, open an issue in the [RomM repository](https://github.com/rommapp/romm/issues/new/choose).

# Project Support

Consider supporting the development of this project on Open Collective.

[![oc-donate-img]][oc-donate]

# Acknowledgement

This plugin is **very heavily** inspired by the excellent [Playnite EmuLibrary](https://github.com/psychonic/Playnite-EmuLibrary) by [@psychonic](https://github.com/psychonic).

<!-- Badges -->

[license-badge-img]: https://img.shields.io/github/license/rommapp/playnite-plugin?style=for-the-badge&color=a32d2a&kill_cache=2
[license-badge]: LICENSE
[release-badge-img]: https://img.shields.io/github/v/release/rommapp/playnite-plugin?style=for-the-badge&kill_cache=2
[release-badge]: https://github.com/rommapp/playnite-plugin/releases
[discord-badge-img]: https://img.shields.io/badge/discord-7289da?style=for-the-badge
[discord-badge]: https://discord.gg/P5HtHnhUDH

<!-- Links -->

[docs-installation-guide]: https://docs.romm.app/latest/Integrations/Playnite-plugin/
[discord-invite]: https://invidget.switchblade.xyz/P5HtHnhUDH
[discord-invite-url]: https://discord.gg/P5HtHnhUDH
[oc-donate-img]: https://opencollective.com/romm/donate/button.png?color=blue
[oc-donate]: https://opencollective.com/romm
