using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Utility class for writing CSV data with consistent formatting and performance optimization.
    /// Handles session ID headers, column headers, buffering, and precision formatting for all recorders.
    /// </summary>
    public class CSVWriter
    {
        private string filePath;
        private StringBuilder buffer;
        private bool isInitialized = false;
        private int bufferFlushThreshold = 100; // Flush after this many rows
        private int currentBufferSize = 0;

        /// <summary>
        /// Initialize CSV writer with file path and headers
        /// </summary>
        /// <param name="directory">Session directory path</param>
        /// <param name="filename">CSV filename (e.g., "Needle.csv")</param>
        /// <param name="sessionID">Session ID for header row</param>
        /// <param name="columnHeaders">Array of column header names</param>
        public CSVWriter(string directory, string filename, string sessionID, string[] columnHeaders)
        {
            // Ensure directory exists
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            filePath = Path.Combine(directory, filename);
            buffer = new StringBuilder();

            // Write session ID header row
            buffer.AppendLine($"SessionID,{sessionID}");

            // Write column headers
            buffer.AppendLine(string.Join(",", columnHeaders));

            isInitialized = true;
            // Debug.Log($"CSVWriter initialized: {filePath}");
        }

        /// <summary>
        /// Add a data row to the buffer
        /// </summary>
        /// <param name="values">Array of values to write</param>
        public void WriteRow(params object[] values)
        {
            if (!isInitialized)
            {
                // Debug.LogError("CSVWriter not initialized!");
                return;
            }

            // Format each value with appropriate precision
            string[] formattedValues = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                formattedValues[i] = FormatValue(values[i]);
            }

            buffer.AppendLine(string.Join(",", formattedValues));
            currentBufferSize++;

            // Flush if buffer is getting large
            if (currentBufferSize >= bufferFlushThreshold)
            {
                Flush();
            }
        }

        /// <summary>
        /// Format individual values with appropriate precision and type handling
        /// </summary>
        private string FormatValue(object value)
        {
            switch (value)
            {
                case float f:
                    // Clamp very small values to zero to eliminate floating point noise
                    if (Mathf.Abs(f) < 1e-7f) f = 0f;
                    return f.ToString("G6", CultureInfo.InvariantCulture); // 6 significant figures
                case double d:
                    // Clamp very small values to zero to eliminate floating point noise
                    if (Math.Abs(d) < 1e-7) d = 0.0;
                    return d.ToString("G6", CultureInfo.InvariantCulture); // 6 significant figures
                case bool b:
                    return b.ToString();
                case int i:
                    return i.ToString();
                case string s:
                    return s;
                default:
                    return value?.ToString() ?? "";
            }
        }

        /// <summary>
        /// Force write buffered data to file
        /// </summary>
        public void Flush()
        {
            if (!isInitialized || buffer.Length == 0)
                return;

            try
            {
                File.AppendAllText(filePath, buffer.ToString());
                buffer.Clear();
                currentBufferSize = 0;
                // Debug.Log($"CSVWriter flushed to: {filePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"CSVWriter flush error ({filePath}): {e.Message}");
            }
        }

        /// <summary>
        /// Finalize and close the CSV file
        /// </summary>
        public void Close()
        {
            Flush(); // Write any remaining data
            isInitialized = false;
            // Debug.Log($"CSVWriter closed: {filePath}");
        }

        /// <summary>
        /// Set the buffer flush threshold (for performance tuning)
        /// </summary>
        public void SetBufferFlushThreshold(int threshold)
        {
            bufferFlushThreshold = Mathf.Max(1, threshold);
        }
    }
}