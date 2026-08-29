# v0.1.4 - Fixes and Compatibility

- Now has Compatibility with [KeepInventory](https://thunderstore.io/c/how-to-fish/p/WhitetailStudios/KeepInventory/) by [WhitetailStudios](https://thunderstore.io/c/how-to-fish/p/WhitetailStudios/)

**Hiccup made the fixes shown below, all the credit for these fixes go to them.**
- Add back some vanilla conditions that could cause issues in multiplayer and syncing
- ItemBeingStored. All four prefix/postfix pairs now save and restore the previous value instead of nulling it, fixing both the nesting clobber and the permanent leak if any patched method throws.
- DestroyByEating. Was an ObserversRpc with no owner check, so every client told the server to deselect the eater's slot. Now guarded to the local owner.
- The bug where buying items too fast could cause the whole stack to become unusable has been fixed.
- In the case you get a "Ghost Item" it will now discard when something in the stack is used/dropped so you may see 2+ items dissapear which is normal.

# v0.1.3 - Changes

![Stacked Weapons - 0.1.3+](https://notice-badges.atomictyler.dev/api/notice?type=warning&title=Stacked+Weapons+-+0.1.3%2B&message=If+you+had+a+stacked+weapon+before+0.1.3%2C+the+item+will+now+go+into+an+open+slot+you+have%2C+if+the+slot+is+empty%2C+it+is+possible+to+**lose+the+item+permanently.**+Luckily%2C+because+of+a+new+game+update+drop+your+weapons+before+updating.&width=640&radius=8)
- You can no longer stack weapons. If you have stacked weapons in your old save then they will be sent to other slots.

# v0.1.2 - Fix

- Accidentally made it so the mod only works when SaveBackups is enabled.
     - No data will be lost if you downloaded without, it just wouldnt load.

# v0.1.1 - Update

- Support for [SaveBackups](https://thunderstore.io/c/how-to-fish/p/hiccup/SaveBackups/) by [hiccup](https://thunderstore.io/c/how-to-fish/p/hiccup/)
- Change the UI to display a stack just like the multiple bait.

# v0.1.0 - Release

- Release of the mod.