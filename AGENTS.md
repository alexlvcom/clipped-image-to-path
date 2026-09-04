# Repository Instructions

- When starting user-visible changes after a release, set the application version to the next intended release version.
- Do not bump the version again for additional changes or testing iterations before that release. Application versions must correspond to published releases, not individual development changes.
- Keep all changes planned for the same unreleased version under one changelog heading.
- After implementing a user-visible app change, publish and deploy it to the local installation, then restart the running app so the user can test the result.
- Keep version values in `ClippedImageToPath.csproj` in sync: `Version` and `InformationalVersion` use `N.N.N`; `FileVersion` and `AssemblyVersion` use `N.N.N.0`.
- Do not include commit hashes, source revision IDs, or other unique build identifiers in the About window version.
