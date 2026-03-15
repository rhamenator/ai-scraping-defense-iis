# Local Model Training

The escalation engine can load a local trained model artifact for self-contained scoring inside the .NET runtime.

## Runtime Configuration

Enable the local model adapter under `DefenseEngine:Escalation:LocalTrainedModel`:

- `Enabled`
- `ModelPath`
- `MetadataPath`
- `RequiredModelVersion`
- `MaliciousProbabilityThreshold`
- `MaliciousScoreAdjustment`
- `BenignScoreAdjustment`

If `MetadataPath` is left empty, the runtime looks for `<ModelPath>.metadata.json`.

## Training Input Format

The trainer consumes JSON Lines (`.jsonl`). Each line is one labeled example:

```json
{"label":true,"method":"GET","path":"/graphql","query_string":"?page=1&take=5000","user_agent":"python-requests/2.31","signals":["known_bad_user_agent:python-requests","suspicious_path:/graphql","missing_accept_language","generic_accept_any","long_query_string"],"frequency":4}
```

Required fields:

- `label`
- `method`
- `path`
- `query_string`
- `user_agent`
- `signals`
- `frequency`

## Training Command

Run the bundled trainer:

```bash
dotnet run --project AiScrapingDefense.ModelTrainer -- \
  --input data/local-model-training.jsonl \
  --output models/local-bot-detector.zip \
  --version 2026.03.15 \
  --threshold 0.75
```

This writes:

- the model archive to the requested `--output` path
- metadata to `<output>.metadata.json`

The metadata file records:

- schema version
- model version
- algorithm
- training timestamp
- malicious threshold
- training example count
- feature-column list

## Loading Behavior

When enabled, the runtime loads the model lazily on first use, validates the metadata schema, and optionally enforces `RequiredModelVersion`.

## Current Scope

This commercial-v1 path provides:

- .NET-native local model inference
- a .NET-native trainer CLI
- explicit model metadata and version checking

The older project’s more opinionated log/feedback dataset-building path is still tracked separately in issue `#64`.
