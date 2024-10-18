﻿using HeavenTool.Utility.IO;
using HeavenTool.Utility.IO.Compression;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HeavenTool.Utility.FileTypes.RSTB;

public class ResourceTable : IDisposable
{
    public class ResourceTableEntry
    {
        public ResourceTableEntry()
        {

        }

        public ResourceTableEntry(uint hash, uint fileSize, bool isDuplicated)
        {
            CRCHash = hash;
            FileSize = fileSize;
            IsDuplicatedEntry = isDuplicated;
        }

        public ResourceTableEntry(string name, uint fileSize, bool isDuplicated)
        {
            CRCHash = name.ToCRC32();
            _fileName = name;
            FileSize = fileSize;
            IsDuplicatedEntry = isDuplicated;
        }


        private bool _isDuplicated;
        public bool IsDuplicatedEntry
        {
            get
            {
                return _isDuplicated;
            }
            set
            {
                _isDuplicated = value;
            }
        }

        private bool unknownHash;
        private string _fileName;

        /// <summary>
        /// File name, max of 128 characters
        /// </summary>
        public string FileName
        {
            get
            {
                if (CRCHash > 0 && _fileName == null && !unknownHash)
                {
                    // Try to get the file name using our files
                    _fileName = RomFsNameManager.GetValue(CRCHash);

                    // If we don't have the "translated hash" set unknown hash to true
                    unknownHash = _fileName != null;
                }

                return _fileName;
            }

            set { _fileName = value; }
        }

        private uint _hash;
        public uint CRCHash
        {
            get
            {
                if (_hash == 0 && !string.IsNullOrEmpty(_fileName))
                    _hash = _fileName.ToCRC32();

                return _hash;
            }

            set { _hash = value; }
        }

        public uint FileSize;

        /// <summary>
        /// Only present on RSTC files, seems to be a DLC number since it's only 1 when it's a file from Happy Home Paradise DLC
        /// </summary>
        public uint DLC;

        /// <summary>
        /// Write entry to binary
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="isRSTC">If the file is RSTC</param>
        public void Write(BinaryWriter writer, bool isRSTC)
        {
            if (IsDuplicatedEntry)
            {
                // This seems stupid but we have to make sure that our byte array is 128 length
                var bytes = Encoding.ASCII.GetBytes(_fileName);
                if (bytes.Length != 128) Array.Resize(ref bytes, 128);

                writer.Write(bytes);
            }
            else
            {
                writer.Write(CRCHash);
            }

            writer.Write(FileSize);

            if (isRSTC) writer.Write(DLC);
        }
    }

    /// <summary>
    /// Yaz0 Compressor Algorithm
    /// </summary>
    private readonly Yaz0CompressionAlgorithm Compressor = new(true);

    /// <summary>
    /// RSTB or RSTC
    /// </summary>
    public string HEADER { get; private set; }

    /// <summary>
    /// Get a <seealso cref="ResourceTableEntry"/> from <seealso cref="Entries"/> using <seealso cref="ResourceTableEntry.FileName"/>
    /// </summary>
    public Dictionary<string, ResourceTableEntry> Dictionary { get; private set; }

    public List<ResourceTableEntry> Entries { get; private set; }

    ///// <summary>
    ///// Get <b>ALL</b> entries, use <see cref="UniqueEntries"/> to get unique entries or <see cref="RepeatedHashesEntries"/> for repeated hashes
    ///// </summary>
    //public List<ResourceTableEntry> Entries { get; private set; }

    /// <summary>
    /// <para>Used when the fileName CRC32 is <b>unique</b>.</para>
    /// Entries in that list does <b>NOT</b> contain the fileName parameter assigned, the CRC should be decrypted using the RomFs folder
    /// </summary>
    public Dictionary<string, ResourceTableEntry> UniqueEntries() => Dictionary.Where(x => !x.Value.IsDuplicatedEntry).ToDictionary(x => x.Key, x => x.Value);
    

    /// <summary>
    /// If have two (or more) file names that have the same hash both are put here
    /// </summary>
    public Dictionary<string, ResourceTableEntry> NonUniqueEntries() => Dictionary.Where(x => x.Value.IsDuplicatedEntry).ToDictionary(x => x.Key, x => x.Value);
    

    public bool IsRSTC => HEADER == "RSTC";

    public bool IsLoaded { get; internal set; }

    /// <summary>
    /// Adds a new entry to the ResourceTable. It will automatically detect for duplicates and move to the right list.
    /// </summary>
    /// <param name="entry"></param>
    public void AddEntry(ResourceTableEntry entry)
    {
        if (entry == null) return;

        if (!string.IsNullOrEmpty(entry.FileName))
            RomFsNameManager.Add(entry.FileName);
        else throw new Exception("FileName is not defined!");

        if (Dictionary.Values.Any(x => x.CRCHash == entry.CRCHash))
        {
            entry.IsDuplicatedEntry = true;

            foreach (var repeatedEntry in Dictionary.Values.Where(x => x.CRCHash == entry.CRCHash))
                repeatedEntry.IsDuplicatedEntry = true;
        }

        Dictionary.TryAdd(entry.FileName, entry);
    }

    public ResourceTable(string path)
    {
        using (var fileStream = File.OpenRead(path))
        {
            var fileHeader = fileStream.ReadString(4, Encoding.ASCII);
            var isDecompressed = fileHeader == "RSTB" || fileHeader == "RSTC";

            fileStream.Position = 0;

            MemoryStream memoryStream = new();

            if (!isDecompressed)
            {
                if (!Yaz0CompressionAlgorithm.TryToDecompress(fileStream, out byte[] decompressedBytes))
                {
                    MessageBox.Show("Failed to decompress Yaz0", "Failed to open", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                memoryStream = new MemoryStream(decompressedBytes);
            }
            else
            {
                // File is not compressed, copy fileStream to our memoryStream
                fileStream.CopyTo(memoryStream);
            }

            // Start actual reading of our RSTB file
            using var reader = new BinaryFileReader(memoryStream);

            HEADER = reader.ReadString(4, Encoding.ASCII);
            if (HEADER != "RSTB" && HEADER != "RSTC")
            {
                MessageBox.Show($"This is not a valid RSTB/RSTC file! ({HEADER})", "Failed to open", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var uniqueEntriesCount = reader.ReadUInt32();
            var repeatedEntriesCount = reader.ReadUInt32();

            Dictionary = [];

            for (int i = 0; i < uniqueEntriesCount; i++)
            {
                var hash = reader.ReadUInt32();
                var fileSize = reader.ReadUInt32();

                var entry = new ResourceTableEntry(hash, fileSize, false);

                if (IsRSTC) entry.DLC = reader.ReadUInt32();

                // If the hash is not found, we gonna throw an error and this should prevent the file from loading
                if (entry.FileName != null)
                    Dictionary.TryAdd(entry.FileName, entry);
                else throw new Exception($"Hash {entry.CRCHash:x} not found!");
            }

            for (int i = 0; i < repeatedEntriesCount; i++)
            {
                var fileName = reader.ReadString(128, Encoding.ASCII);
                var fileSize = reader.ReadUInt32(); ;

                var entry = new ResourceTableEntry(fileName, fileSize, true);

                if (IsRSTC) entry.DLC = reader.ReadUInt32();

                if (entry.FileName != null)
                    Dictionary.TryAdd(entry.FileName, entry);
            }
        }

        IsLoaded = true;

        UpdateUniques();

        var nonUniqueEntries = Dictionary.Where(x => x.Value.IsDuplicatedEntry).ToList();
        MessageBox.Show(Dictionary.Count(x => x.Value.IsDuplicatedEntry).ToString());
    }

    /// <summary>
    /// This function will update <seealso cref="ResourceTableEntry.IsDuplicatedEntry"/> values in <seealso cref="Dictionary"/>.
    /// </summary>
    public void UpdateUniques()
    {
        if (!IsLoaded) return;

        var groups = Dictionary.GroupBy(x => x.Value.CRCHash);

        foreach(var group in groups)
        {
            var count = group.Count();

            foreach(var (_, entry) in group)
                entry.IsDuplicatedEntry = count > 1;
            
        }
    }

    /// <summary>
    /// Save the file
    /// </summary>
    /// <param name="filePath">File Location</param>
    public void SaveTo(string filePath)
    {
        //var orderableDictionary = Dictionary.OrderBy(x => x.Key).GroupBy(x => x.Value.CRCHash);

        //var nonUniqueEntries = orderableDictionary.Where(x => x.Count() > 1).SelectMany(x => x.Select(y => y.Value)).ToList();
        //var uniqueEntries = orderableDictionary.Where(x => x.Count() == 1).SelectMany(x => x.Select(y => y.Value)).ToList();

        UpdateUniques();

        var uniqueEntries = Dictionary.Where(x => !x.Value.IsDuplicatedEntry).ToList();
        var nonUniqueEntries = Dictionary.Where(x => x.Value.IsDuplicatedEntry).ToList();

        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        // Write header
        writer.Write(Encoding.ASCII.GetBytes(HEADER));

        writer.Write(uniqueEntries.Count);
        writer.Write(nonUniqueEntries.Count);

        foreach (var (_, entry) in uniqueEntries)
            entry.Write(writer, IsRSTC);

        foreach (var (_, entry) in nonUniqueEntries)
            entry.Write(writer, IsRSTC);


        byte[] array = new byte[memoryStream.Length];
        memoryStream.Seek(0L, SeekOrigin.Begin);
        memoryStream.Read(array, 0, array.Length);

        var result = Compressor.Compress(array);

        using var fileStream = File.OpenWrite(filePath);
        fileStream.Write(result);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private bool disposed = false;
    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                IsLoaded = false;
                Dictionary.Clear();
                Dictionary = null;
            }

            // Indicate that the instance has been disposed.
            disposed = true;
        }
    }
}