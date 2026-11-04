# Releasing TreeSpitter

The packages are released together, with the same version: `TreeSpitter` (the library), `TreeSpitter.Generators` (the
source generator), `TreeSpitter.Languages` (the precompiled parsers) and `TreeSpitter.Cli` (the `treespitter` .NET
tool). A release is a `vX.Y.Z` tag on a commit of `main`; CI does the rest. Nobody packs or pushes
packages by hand.

## One-time setup

- nuget.org: a trusted publishing policy for the `sebastienros` account, repository `sebastienros/treespitter`,
  workflow `publish.yml`, environment `release`. The workflow exchanges its GitHub OIDC token for a short-lived API key
  (`NuGet/login`); there is no long-lived nuget.org key in the repository.
- GitHub: an environment named `release` (add required reviewers to make each release wait for an approval).
- Feedz: the `FEEDZ_IO_API_KEY` repository secret, for previews. Without it the preview push is skipped.

## Before tagging

1. `main` is green: `build.yml` (ubuntu and windows: build, tests, formatting, packing).
2. `CHANGELOG.md` has a section for the version, describing what changed for users.
3. `VersionPrefix` in `Directory.Build.props` is the next version, for the previews built after the release.

## Tagging

Tag the release commit of `main` with an annotated tag and push the tag:

```bash
git fetch origin
```

```bash
git tag -a v0.1.0 -m "TreeSpitter 0.1.0" origin/main
```

```bash
git push origin v0.1.0
```

Tags are semantic versions: `v1.2.3`, or `v1.2.3-preview.1` for a prerelease on nuget.org.

## What CI does

[`publish.yml`](../.github/workflows/publish.yml) runs on `v*.*.*` tags: it checks that the tag is a semantic version
on a commit of `main`, builds and tests with the version of the tag, packs the packages, attests their build
provenance, and pushes them to nuget.org.

[`build.yml`](../.github/workflows/build.yml) runs on pull requests and on `main`. On `main` it pushes
`<VersionPrefix>-preview.<run number>` packages to the Feedz feed
`https://f.feedz.io/sebastienros/treespitter/nuget/index.json`.
