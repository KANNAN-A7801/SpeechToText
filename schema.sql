CREATE DATABASE IF NOT EXISTS speech_db CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE speech_db;
-- utf8mb4 is required so Hindi (Devanagari) text is stored correctly
CREATE TABLE IF NOT EXISTS Transcriptions (
  Id INT AUTO_INCREMENT PRIMARY KEY,
  Text LONGTEXT NOT NULL,
  Language VARCHAR(50) NOT NULL DEFAULT 'Hindi + English',
  CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UpdatedAt DATETIME NULL
) CHARACTER SET utf8mb4;
