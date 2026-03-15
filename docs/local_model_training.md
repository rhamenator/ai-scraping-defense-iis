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

## Building a Dataset

The bundled CLI can derive labeled training examples from either:

- W3C request logs with a `#Fields:` header
- request-export JSON Lines (`.jsonl`) files using the schema below

Optional operator feedback can be folded into the dataset as JSON Lines overrides. Each feedback line can match on IP alone or more specifically on IP plus path, user agent, and observed timestamp.

Request-export example:

```json
{"timestamp_utc":"2026-03-15T12:00:00Z","ip_address":"198.51.100.30","method":"GET","path":"/graphql","query_string":"?page=1&take=5000","user_agent":"python-requests/2.31","accept":"*/*","accept_language":"","frequency":4}
```

Feedback example:

```json
{"label":true,"ip_address":"198.51.100.30","path":"/graphql","user_agent":"python-requests/2.31","observed_at_utc":"2026-03-15T12:00:00Z","note":"confirmed scraper"}
```

Build a dataset from request-export JSONL:

```bash
dotnet run --project AiScrapingDefense.ModelTrainer -- \
  build-dataset \
  --input-format request-jsonl \
  --input data/request-export.jsonl \
  --feedback data/operator-feedback.jsonl \
  --output data/local-model-training.jsonl
```

Build a dataset from a W3C log:

```bash
dotnet run --project AiScrapingDefense.ModelTrainer -- \
  build-dataset \
  --input-format w3c \
  --input data/u_ex260315.log \
  --feedback data/operator-feedback.jsonl \
  --output data/local-model-training.jsonl
```

The builder applies operator feedback first and then fills the remaining dataset with high-confidence heuristic labels.

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
  train \
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
- a .NET-native dataset builder for W3C logs or request-export JSONL plus operator feedback
- explicit model metadata and version checking
