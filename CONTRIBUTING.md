# Contributing to Duplimate

Thanks for your interest. Duplimate is MIT-licensed, so contributions are simple — by submitting a pull request, you agree your contribution is offered under the same [MIT License](LICENSE) that covers the rest of the project. That's the standard "inbound = outbound" rule and is implicit in opening a PR; no separate CLA needed.

## Reporting bugs / requesting features

Open an issue. Include:

- what you expected to happen
- what actually happened
- steps to reproduce
- your OS version, Duplimate version, and any relevant log excerpts

## Submitting code

1. **Discuss first for non-trivial changes.** For anything more than a small bugfix or polish, open an issue to discuss the approach before writing the code. Saves both of us time.
2. **Fork, branch, write tests.** Duplimate expects new functionality (and bugfixes) to come with regression coverage. The test suite must stay green.
3. **Match existing style.** Follow the patterns already used in `src/Duplimate/`. Don't introduce new abstractions or dependencies without discussing it first.
4. **Keep PRs focused.** One logical change per PR. Don't bundle unrelated cleanup with a feature change.
5. **Run the build & tests locally** before pushing.
6. **Open a PR** against `main`.

## Code review

A maintainer will review and either merge, request changes, or explain why the PR is being declined. Reviews are usually direct — that's not personal, it's just the bar for shipped code.

## Originality

By submitting, you represent that the contribution is your original work (or is properly attributed and you have the right to submit it under MIT), and that your employer (if any) has waived any rights in it or has authorized you to submit it.

## Questions

If anything is unclear, open an issue and ask.
