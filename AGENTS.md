# Instructions for Agents

## Guidelines

- Never automatically create a new release entry in CHANGELOG.md, instead on substantial updates add notes to the Unreleased section.
- For the release process, follow the steps documented in CONTRIBUTING.md § "Making a release".
- Avoid external dependencies, like Azure SDKs, instead directly interact with the REST apis.
- Cold-start is of utmost importance, avoid expensive code in the startup code paths.
- The entire program is statically built, avoid reflection and use code generation or hard coded things instead.
- Reflection-based serialization for JSON has been disabled for this application, use the source-generator APIs instead. Do not manually write JSON.
- We do not care about backwards-compatibility when changing code, nothing is consumed elsewhere so we just modify the code and all its call-sites.
- Do not use DefaultAzureCredential from the Azure SDK, instead we use AuthOptionPack.GetCredential() to get the correct credential.
- Avoid the Azure SDK to access Azure Resource Manager instead use AzureRestClient and hand-crafted DTOs to deserialize.