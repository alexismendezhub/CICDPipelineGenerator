# Schemas

The JSON schemas the generated files are checked against in the tests (`SchemaTests.cs`).

| File | From | License |
|---|---|---|
| `gitlab-ci.json` | GitLab, `app/assets/javascripts/editor/schema/ci.json` (gitlab.com/gitlab-org/gitlab) | MIT |
| `azure-pipelines.json` | Microsoft, `service-schema.json` (github.com/microsoft/azure-pipelines-vscode) | MIT |

| `circleci.json` | CircleCI, `schema.json` (github.com/CircleCI-Public/circleci-yaml-language-server) | Apache-2.0 |

They are copies; to update them, download the files again.

`bitbucket-pipelines.json` (https://api.bitbucket.org/schemas/pipelines-configuration) is published without a
licence, so it is not kept in the repository: download it into this folder and the tests check
`bitbucket-pipelines.yml` against it; without it that check is skipped.
