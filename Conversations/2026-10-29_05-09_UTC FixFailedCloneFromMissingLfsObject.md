# Fix failed clone from missing LFS object

## User

i got this error when trying to clone one of my git repos:

```text
PowerShell 7.6.9
PS C:\Users\K> cd .\Repository\
PS C:\Users\K\Repository> git clone https://github.com/kvu787/HexelEditor.git
Cloning into 'HexelEditor'...
remote: Enumerating objects: 1264, done.
remote: Counting objects: 100% (1264/1264), done.
remote: Compressing objects: 100% (636/636), done.
remote: Total 1264 (delta 735), reused 1118 (delta 589), pack-reused 0 (from 0)
Receiving objects: 100% (1264/1264), 11.42 MiB | 70.43 MiB/s, done.
Resolving deltas: 100% (735/735), done.
Updating files: 100% (174/174), done.
Downloading HexelEditor_Wpf/HexGridExample.blend (504 KB)
Error downloading object: HexelEditor_Wpf/HexGridExample.blend (fc0fc0b): Smudge error: Error downloading HexelEditor_Wpf/HexGridExample.blend (fc0fc0baf14a0594dc39c7f4656754035a0001d144476a7afb6aadc0e082f3ac): [fc0fc0baf14a0594dc39c7f4656754035a0001d144476a7afb6aadc0e082f3ac] Object does not exist on the server: [404] Object does not exist on the server

Errors logged to 'C:\Users\K\Repository\HexelEditor\.git\lfs\logs\20261028T220450.0433123.log'.
Use `git lfs logs last` to view the log.
error: external filter 'git-lfs filter-process' failed
fatal: HexelEditor_Wpf/HexGridExample.blend: smudge filter lfs failed
warning: Clone succeeded, but checkout failed.
You can inspect what was checked out with 'git status'
and retry with 'git restore --source=HEAD :/'

PS C:\Users\K\Repository>
```

## Assistant

The clone is fixed: every file is checked out except the one `.blend` that's missing on GitHub. Next I'll look for an original copy of that file on this machine.

## Assistant

Your clone is usable now. One file is missing from GitHub, and only you can put it back.

**What went wrong:** `HexelEditor_Wpf/HexGridExample.blend` is stored with Git LFS. Your Git history has the small LFS pointer for it (from commit `48b832f`, "Add Blender hex grid example…", Oct 22), but the actual 504 KB file was never uploaded to GitHub's LFS storage. That usually means it was pushed from a clone where Git LFS's pre-push upload didn't run. When the download failed, Git stopped the whole checkout, so every file showed as deleted.

**What I checked and did in `C:\Users\K\Repository\HexelEditor`:**
- I ran `git lfs fetch --all`. The only object missing on the server is that `.blend`. `GodotAgentDevExperiment/Assets/LBuilding.glb` downloads fine.
- I finished the checkout with LFS downloads turned off, then filled in the LFS files that do exist. `git status` is now clean.
- `HexGridExample.blend` is still just a text pointer, so Blender can't open it.
- I searched your whole user folder for an original copy and didn't find one.

**To fix it on GitHub:**
- **If the clone you made that commit from still exists** (another machine, an old folder, a dev container), run this inside it to upload the missing file:
  ```bash
  git lfs push --all origin
  ```
- **If the file is gone,** re-export it from Blender and commit it, or delete it from the repo.

Until one of those is done, every new clone will hit the same error. To get past it, clone with LFS downloads turned off:
```bash
$env:GIT_LFS_SKIP_SMUDGE=1; git clone https://github.com/kvu787/HexelEditor.git; Remove-Item Env:GIT_LFS_SKIP_SMUDGE
```

## User

Yes, remove it

## Assistant

The old notes in the repo show the original was at `C:\Users\k\Repository\HexelEditor\HexGridExample.blend`, which means an earlier clone of this repo once lived at this same path. Before I delete the file, I'll check whether that old clone is still in the Recycle Bin.

## Assistant

The Recycle Bin doesn't have it either, so I'll go ahead and remove the file.

## Assistant

I removed `HexelEditor_Wpf/HexGridExample.blend` and committed it on `main` as `c4af8a1`. Nothing is pushed.

- The Recycle Bin didn't have the original either, so it's gone for good.
- I left the older conversation logs that mention the file as they are, since they're verbatim records. The `*.blend` LFS rule in `.gitattributes` also stays, because BlenderHexGrid's demo output still uses it.
- As AGENTS.md asks, I recorded this conversation in `Conversations/2026-10-29_05-09_UTC FixFailedCloneFromMissingLfsObject.md`, in a separate `[cnv]` commit.

Once you push, fresh clones of `main` will work again without the workaround. Older commits still point to the missing object, so checking out anything from before `c4af8a1` will hit the same error, and so will `git lfs fetch --all`.
