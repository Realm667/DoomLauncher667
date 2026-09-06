# Migration from the original Doom Launcher

Migration is offered when starting a new Doom Launcher 667 installation. It is
separate from updating an existing 667 installation. There is no permanent
migration button in the main toolbar.

1. Close the original launcher and back up its complete installation.
2. Extract Doom Launcher 667 into a **different, separate folder**. Neither
   installation should be nested inside the other.
3. At first launch select **Migrate**, then choose the original folder containing
   `DoomLauncher.sqlite`, not just its `GameFiles` folder.
4. The launcher copies the compatible database and available managed/referenced
   files into the 667 portable structure. This requires additional disk space.
   Wait for progress and the completion message before opening the library.

The process covers available IWADs, source ports, mod archives, title artwork,
screenshots and metadata supported by Doom Launcher 667. Files are copied,
not moved out of the original installation; paths are adapted for the destination.
Compatibility repairs upgrade the copied database. Existing custom placeholder
images at the destination are preserved. Handled errors/cancellation roll back
file replacements; keep your independent backup for storage/power failures.

Missing source files cannot be reconstructed. Original-only features and engine
configuration formats are not automatically converted into new functionality.
After completion, review counts, artwork and launch definitions; launch a known
mod before retiring the original installation. Do not delete the original merely
because the copy operation completed.

If you already skipped migration and populated 667, do not reset your active
library to try again. Use a separate fresh 667 folder for the migration instead.
