# Texture Set Maker (TSMaker)
A lightweight utility to speed up the process of creating a resource pack for Minecraft RTX by allowing you to quickly generate texture_set.json files.  
Requires .NET +6.0 Runtime installed.

# How To Use
After running TextureSetMaker.exe, you will need to input the full directory of textures/blocks folder within your resource pack, all of the files will be automatically created & placed in your textures/blocks folder.  

Files names that already contain suffixes used by TSMaker are ignored, so you can safely use it on existing PBR resource packs if they follow standard naming conventions.  

Jsons for textures in blocks folder's subdirectories (e.g. textures in textures/blocks/deepslate directory) are placed in their own folder, in other words. Folder structure is preserved.  

Your textures are copied with proper suffixes that match the texture_set json files, so you may edit those instead and put everything in one folder after you are done editing.

# Roadmap
This tool will be moved to WinUI 3.0 in the future with more tools to generate and manage texture sets and speed up more parts of authoring a PBR resource pack.
