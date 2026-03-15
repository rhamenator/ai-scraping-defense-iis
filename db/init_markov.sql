CREATE TABLE IF NOT EXISTS markov_words
(
    id SERIAL PRIMARY KEY,
    word TEXT UNIQUE NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_markov_words_word
    ON markov_words (word);

INSERT INTO markov_words (word)
VALUES ('')
ON CONFLICT (word) DO NOTHING;

CREATE TABLE IF NOT EXISTS markov_sequences
(
    p1 INT NOT NULL REFERENCES markov_words(id),
    p2 INT NOT NULL REFERENCES markov_words(id),
    next_id INT NOT NULL REFERENCES markov_words(id),
    freq INT NOT NULL DEFAULT 1,
    CONSTRAINT uq_markov_sequence UNIQUE (p1, p2, next_id)
);

CREATE INDEX IF NOT EXISTS idx_markov_sequences_prev
    ON markov_sequences (p1, p2);

CREATE INDEX IF NOT EXISTS idx_markov_sequences_freq
    ON markov_sequences (p1, p2, freq DESC);
