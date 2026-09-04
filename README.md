# Texture Set Manager
Feature-rich automation tool for Minecraft Bedrock Edition resource pack authors to generate texture sets for RTX or Vibrant Visuals.

<!-- Microsoft Store badge -->
<p align="center">
  <a href="https://apps.microsoft.com/detail/9P9HP6ZL1981?referrer=appbadge&mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="400"/>
  </a>
</p>
<!-- Cover image -->
<p align="center">
  <img src="https://github.com/user-attachments/assets/3a1b5d90-7720-4412-9d72-4a0fa64f6827" alt="texture-set-manager-cover"/>
</p>
<!-- Badges -->
<p align="center">
  <a href="https://discord.gg/A4wv4wwYud">
    <img src="https://img.shields.io/discord/721377277480402985?style=flat-square&logo=discord&logoColor=F4E9D3&label=Discord&color=F4E9D3&cacheSeconds=3600"/>
  </a>
  <a href="https://ko-fi.com/cubeir">
    <img src="https://img.shields.io/badge/-support%20my%20work-F4E9D3?style=flat-square&logo=ko-fi&logoColor=F4E9D3&labelColor=555555"/>
  </a>
  <img src="https://img.shields.io/github/repo-size/Cubeir/Texture-Set-Manager?style=flat-square&color=F4E9D3&label=Repo%20Size&cacheSeconds=3600"/>
  <img src="https://img.shields.io/github/last-commit/Cubeir/Texture-Set-Manager?style=flat-square&color=F4E9D3&label=Last%20Commit&cacheSeconds=1800"/>
</p>

# Overview

- Select a folder and any number of individual files to be processed together (supports drag-and-drop).
> Use the clear button to clear selections, selections are also cleared after each generation attempt.
- Enable or disable Subsurface Scattering property.
- Select secondary PBR texture type, this could either be a Normal Map or a Heightmap, set to none to generate MER(s) only.
- Process Subfolders: If enabled, all subfolders of the selected folder are also processed.
- Smart Filters: Excludes PBR texture generation for files that are already part of a PBR texture set, this is figured out in in two passes:  
  1. Files that already end with the the conventional PBR texture suffixes (`_mer(s)`, `_normal` or `_heightmap`) are excluded
  > Something to be aware of: It also attempts to figures out whether the _normal suffix indicates a _block variant_ as opposed to a _normal PBR texture_, so there's lesser chance of falsely excluding color textures! e.g. sand_normal isn't excluded if sand_normal_normal exists.
  2. All existing `*.texture_set.json` files are parsed and files referenced by them are excluded as well.
  > In other words, any file that may already belong to a texture set will be excluded from texture set generation. This means Texture Set Manager can safely be reused on existing texture packs to mend files and generate missing texture sets with ease.
- Convert to TGA: Decides the format the whole texture set ends up in – a raw 32-bit (8-bit per RGBA channel) Targa, ideal if your resource pack targets RTX (Windows-only) or if your editing software works better with this format for storing color with alpha channel flattened to zero.
  - **On**: a color texture that isn't already `.tga` is converted first and the original is deleted, so the PBR templates copied from it come out as `.tga` too. Color textures that are already `.tga` are left byte-for-byte alone – nothing is re-encoded.
  - **Off**: nothing is converted, and the PBR templates simply inherit whatever format the color texture already is.
- Create Backup: Allows you to quickly export the selected folder and files as a zip file in case something goes wrong, recommended to use until you get used to the output of Texture Set Manager.
- Create New Folders: Collects everything a run generates into a new subfolder of each processed folder instead of writing it next to your color textures. The folder is named after the secondary PBR texture type – `Heightmaps-PBR`, `Normals-PBR` or `None-PBR` – so `textures/blocks/deepslate` gets a `textures/blocks/deepslate/Heightmaps-PBR` holding that run's texture sets and templates. Handy for keeping generated output separate from the art you already have while you work through it.

After configuring your generation parameters, press generate button for texture set jsons and PBR texture templates to be created adjacent to the given textures.

Now you have a readily-available template to work on! 🎉  
Modify the textures ending with `_mer(s)`, `_normal` or `_heightmap` suffixes to shape up your PBR resource pack!
If you need more info, check out the [official Bedrock Edition PBR documentation](https://learn.microsoft.com/en-us/minecraft/creator/documents/vibrantvisuals/pbroverview?view=minecraft-bedrock-stable).

## Other texture set management tools in the title bar

The developer-tools button in the title bar opens a menu with two tools that work on an existing pack rather than generating a new one. Both ask for a folder first.

### Analyze texture sets
Reads every `*.texture_set.json` in the chosen folder and all of its subfolders, then compares each MER/MERS, normal and heightmap against the color texture of its own set. Any PBR texture that is still **pixel-for-pixel identical to its color texture** is flagged – those are almost always template copies that never got painted by an artist, which produce nonsense PBR data in-game and quietly bloat the pack. Texture sets that reference a file which doesn't exist are reported too.

The result opens in a dialog with a copy button and is also written to the log. This tool only reads: it never modifies or deletes anything.

### Strip all texture sets
Deletes every texture set in the chosen folder along with the MER/MERS, normal and heightmap textures those sets reference, leaving **only your color textures**. Useful for starting a pack's PBR over from scratch, or for clearing out a pack that declares PBR support but ships little real PBR content.

Before anything is deleted you're asked whether to strip just that folder or to include its subfolders – that prompt is also the confirmation step, and cancelling is its default. **There is no undo**, so take a backup first if you're unsure.

It only ever deletes what a texture set actually resolved to as a PBR layer – never a name-pattern sweep. That matters: plenty of legitimate *color* textures end in `_normal` (`sandstone_normal`, `rail_turned_normal`) where "normal" means the ordinary variant of a block, and guessing by suffix would delete those outright.

---

For app support, head over to the [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud) server's forum channel, or open an issue here.
Click the log section beneath the generate button to copy debug logs, plenty useful to attach these when reporting issues!
If the app ever crashes, it writes a report and shows it to you on the next launch with a button to copy it – attaching that makes a bug easier to fix.