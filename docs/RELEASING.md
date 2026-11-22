# Releasing Race Mode

Two branches, like Jellyfin's release candidates:

| Branch | What's on it | Release it makes | Who gets it |
|---|---|---|---|
| `dev` | Features that passed review in a pull request, still being tested together | `v1.2.0-rc.1`, `rc.2`… (pre-release) | People with **Try test versions** on (Settings tab → Updates) |
| `main` | Only release candidates that passed testing | `v1.2.0` (stable) | Everyone |

Nothing is ever pushed straight to `dev` or `main`. Every change arrives in a pull request, and the **Checks** workflow (build + tests) has to pass first.

## Version numbers

The first public release is `1.0.0`. After that, every update is a 1.x release:

- `1.0.1`, `1.0.2`… bug fixes only.
- `1.1.0`, `1.2.0`… new features (the work merged into `dev`).
- Release candidates add `-rc.N` to the version they're heading for, e.g. `v1.1.0-rc.1`.

A `2.0.0` would only be for a change that breaks existing settings or setups.

## The flow

1. **New feature or fix:** make a branch from `dev` (e.g. `feature/obs-dock`), push it, open a pull request into `dev`. Checks run. Merge when they pass and the change is reviewed.
2. **Release candidate:** when `dev` has enough for a release, tag the newest `dev` commit `v1.2.0-rc.1` and push the tag. The **Release** workflow builds it, runs the tests and publishes a pre-release. Test versions pick it up within a day (or straight away with **Check Now**).
3. **Problems found:** fix them in pull requests into `dev`, then tag `v1.2.0-rc.2`. Repeat until it's clean.
4. **Stable:** open a pull request from `dev` into `main`. Merge it, tag that `main` commit `v1.2.0`, push the tag. Everyone's Race Mode offers it.
5. **Urgent fix for the stable version:** branch from `main`, pull request into `main`, tag `v1.2.1`. Then merge `main` back into `dev` so the fix isn't lost.

The Release workflow refuses a tag that's in the wrong place: an `-rc.N` tag must be on a commit made on `dev` itself, and a plain tag on a commit made on `main` itself. A commit that only reached the branch through a merge (a feature branch's own commit) doesn't count. The real gate is the tag rule below: only the owner can create `v*` tags.

The workflow builds and tests with a read-only token. A separate job, which runs none of the repo's code, publishes the release.

### Tag commands

```
git checkout dev
git pull
git tag v1.2.0-rc.1
git push origin v1.2.0-rc.1
```

Stable is the same on `main`, with a tag such as `v1.2.0`.

## How the app decides what to offer

- Stable users get GitHub's latest stable release.
- Test-version users get the newest of the stable releases and the release candidates. So when `v1.2.0` ships, Race Mode offers it to them instead of `v1.2.0-rc.3`; they still click Update.
- A version is only offered if its release has both `RaceMode.zip` and `RaceMode.zip.sha256` from this repo. The download must match the checksum, and the exe inside must be Race Mode at that exact version.
- Turning test versions off doesn't downgrade anyone. They stay on the release candidate until the next stable release is newer.

## One-time GitHub setup

Done once, when the repo is created:

- **Private email:** in GitHub, go to Settings → Emails. Turn on "Keep my email addresses private" and "Block command line pushes that expose my email". Commits use the `…@users.noreply.github.com` address.
- **Git identity, before the first commit.** The private-email setting does **not** rewrite commits made on this PC; they carry whatever name and email git has locally. In the project folder:

  ```
  git config --local user.name 1sharkcs
  git config --local user.email <ID>+1sharkcs@users.noreply.github.com
  git config --local user.useConfigOnly true
  ```

  `<ID>` is the number shown next to the noreply address in Settings → Emails. After committing, check with `git log --format="%an <%ae> | %cn <%ce>"`: every line must show the noreply address.
- **Optional:** to avoid publishing your local time zone offset in commit dates, commit with a UTC time zone, e.g. in Command Prompt `set TZ=UTC` before `git commit`.
- **Branch rules** (Settings → Rules → Rulesets), for both `main` and `dev`:
  - require a pull request before merging;
  - require the **Checks / build-and-test** status check;
  - require one approving review, so someone else's pull request (including one that edits the workflows) can't merge itself; add yourself to the ruleset's bypass list, because GitHub doesn't let you approve your own pull requests. The owner's Repository-admin bypass is for emergencies only: it doesn't change the rule that nothing is pushed straight to `dev` or `main`;
  - tick **Require review from Code Owners**, so a change to `.github/`, `build.cmd` or `install.cmd` (the files listed in `.github/CODEOWNERS`) needs the owner's review;
  - block force pushes;
  - restrict deletions.
- **Tag rule:** a ruleset on tags matching `v*` that restricts creating, updating and deleting them, with only the owner allowed to bypass it.
- **Actions** (Settings → Actions → General):
  - set "Workflow permissions" to read-only (the Release workflow asks for write access itself);
  - set "Fork pull request workflows" to "Require approval for all external contributors";
  - under "Actions permissions", allow only actions created by GitHub (the workflows use `actions/checkout`, `actions/upload-artifact` and `actions/download-artifact`).
- **Merging:** allow merge commits (or squash), not rebase merging, so `dev` and `main` keep their own first-parent history for the tag check.

## First publish, in order

The repo starts **Private** and only goes Public at the very end, after a release has been checked.

1. Create the GitHub repo as **Private** (no README, no license, no .gitignore from GitHub).
2. Set the git identity (above), in the project folder.
3. `git init -b main`
4. `git add -A`
5. `git ls-files` must list exactly the expected public files and nothing else: `src`, `assets`, `tests` (not `tests-local`), `docs`, `.github`, `build.cmd`, `install.cmd`, `README.md`, `.gitignore`, `.gitattributes`. If you see `config.json`, `HANDOFF.md`, `PRODUCT.md`, `DESIGN.md`, `tests-local`, `tools`, `previews`, `logs`, `graphics`, any `*.exe`, `.claude` or `.impeccable`, stop and fix `.gitignore` before committing.
6. `git commit -m "First commit"`, then check the author with `git log --format="%an <%ae> | %cn <%ce>"`.
7. `git push -u origin main`
8. Create `dev` from `main` and push it.
9. Set up the rulesets for `main`, `dev` and the `v*` tags (see One-time GitHub setup above), including **Require review from Code Owners**.
10. Turn on release immutability: Settings → General → Releases, "Enable release immutability", so a published release's files can't be swapped later.
11. Pin the actions in `.github/workflows` to full commit SHAs (a pull request into `dev`, with the version as a comment after each SHA).
12. Tag `v1.0.0` and push the tag.
13. Check the release: it has `RaceMode.zip`, `RaceMode.zip.sha256` and `install.cmd`; run the install command from the README on a spare PC or account and confirm the updater sees it.
14. Only then make the repo **Public**.

## Things not to change

- The `.sha256` file must stay ASCII, exactly `<64 hex>  RaceMode.zip` (two spaces). `install.cmd` rejects anything else, and the in-app updater needs the same form.
- Don't rename or transfer the repo, or rename the account, after a release. The updater only trusts this repo's name (`BuildInfo.Repo`), so every installed copy would stop updating.
