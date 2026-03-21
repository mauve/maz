# Instructions for Agents

## Guidelines

- Never automatically create a new release entry in CHANGELOG.md, instead on substantial updates add notes to the Unreleased section.
- Avoid external dependencies, like Azure SDKs, instead directly interact with the REST apis.
- Cold-start is of utmost importance, avoid expensive code in the startup code paths.
- The entire program is statically built, avoid reflection and use code generation or hard coded things instead.
- Reflection-based serialization for JSON has been disabled for this application, use the source-generator APIs instead. Do not manually write JSON.