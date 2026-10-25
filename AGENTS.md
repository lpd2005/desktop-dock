# Repository guidance

## Scope

This repository contains a planned Windows 11 desktop Dock application. Read README.md and docs/ROADMAP.md before implementing changes. The initial repository setup does not authorize starting application development; wait for the user to approve the development plan.

## Public repository and privacy

- Treat all tracked files, commit messages, and commit metadata as public.
- Never commit credentials, private keys, signing certificates, real user data, machine-specific configuration, private contact details, local filesystem paths, logs, crash dumps, or chat transcripts.
- Use sanitized placeholders in examples. Review screenshots and release archives before publishing them.
- Stage explicit project files and inspect the staged diff and file list before committing. Do not assume .gitignore proves a file is safe.
- Use a repository-local Git identity with the owner's GitHub noreply address. Do not change global Git identity or expose a private email through author or committer metadata.
- Check existing tracked files separately: adding an ignore pattern does not remove previously tracked content.

## Versioning

Follow docs/PUBLISHING.md. Keep CHANGELOG.md accurate. Do not create application release tags or claim functionality exists until a runnable build has been verified.
