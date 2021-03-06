﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DBDefsDumper
{
    class Program
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Header
        {
            public UInt32 magic;
            public UInt32 cpu;
            public UInt32 subtype;

            public UInt32 fileType;

            public UInt32 commandsCount;

            public UInt32 commandsSize;
            public UInt32 flags;

            public UInt32 reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Command
        {
            public UInt32 id;
            public UInt32 size;
        }

        struct Map
        {
            public UInt64 memoryStart;
            public UInt64 memoryEnd;
            public UInt64 fileStart;
            public UInt64 fileEnd;

            public UInt64 translateMemoryToFile(UInt64 offset)
            {
                if (offset < memoryStart || offset > memoryEnd)
                {
                    return 0x0;
                }

                var delta = offset - memoryStart;

                return fileStart + delta;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                throw new ArgumentException("Not enough arguments! Required: file");
            }

            if (!File.Exists(args[0]))
            {
                throw new FileNotFoundException("File not found!");
            }

            var file = MemoryMappedFile.CreateFromFile(args[0], FileMode.Open);
            var stream = file.CreateViewAccessor();

            #region BinaryMagic 
            long offset = 0;

            stream.Read(offset, out Header header);
            offset += Marshal.SizeOf(header);

            var maps = new List<Map>();

            for (var i = 0; i < header.commandsCount; i++)
            {
                stream.Read(offset, out Command command);

                if (command.id == 25)
                {
                    var segmentOffset = offset + 4 + 4 + 16; // Segment start + id + size + name; 
                    var vmemOffs = stream.ReadUInt64(segmentOffset);
                    var vmemSize = stream.ReadUInt64(segmentOffset + 8);
                    var fileOffs = stream.ReadUInt64(segmentOffset + 16);
                    var fileSize = stream.ReadUInt64(segmentOffset + 24);

                    var map = new Map
                    {
                        memoryStart = vmemOffs,
                        memoryEnd = vmemOffs + vmemSize,
                        fileStart = fileOffs,
                        fileEnd = fileOffs + fileSize
                    };

                    maps.Add(map);
                }

                offset += command.size;
            }


            Func<UInt64, UInt64> translate = search => maps.Select(map => map.translateMemoryToFile(search)).FirstOrDefault(r => r != 0x0);
            #endregion

            using (var bin = new BinaryReader(file.CreateViewStream()))
            {
                var chunkSize = 1024;

                // Find version
                var buildPattern = new byte?[] { 0x20, 0x5B, 0x42, 0x75, 0x69, 0x6C, 0x64, 0x20, null, null, null, null, null, 0x20, 0x28, null, null, null, null, null, 0x29, 0x20, 0x28 };
                var buildPatternLength = buildPattern.Length;

                var build = "";

                while (true)
                {
                    if ((bin.BaseStream.Length - bin.BaseStream.Position) < chunkSize)
                    {
                        break;
                    }

                    var posInStack = Search(bin.ReadBytes(chunkSize), buildPattern);

                    if (posInStack != chunkSize)
                    {
                        var matchPos = bin.BaseStream.Position - chunkSize + posInStack;

                        bin.BaseStream.Position = matchPos;
                        bin.ReadBytes(8);
                        var buildNumber = new string(bin.ReadChars(5));
                        bin.ReadBytes(2);
                        var patch = new string(bin.ReadChars(5));
                        build = patch + "." + buildNumber;
                    }
                    else
                    {
                        bin.BaseStream.Position = bin.BaseStream.Position - buildPatternLength;
                    }
                }


                if (build == "")
                {
                    throw new Exception("Build was not found!");
                }

                // Reset position for DBMeta reading
                bin.BaseStream.Position = 0;

                // Extract DBMeta
                var metas = new Dictionary<string, DBMeta>();

                var patternBuilder = new PatternBuilder();

                foreach(var pattern in patternBuilder.patterns)
                {
                    // Skip versions of the pattern that aren't for this expansion
                    if(build[0] != pattern.name[0])
                    {
                        continue;
                    }

                    var patternBytes = ParsePattern(pattern.cur_pattern).ToArray();
                    var patternLength = patternBytes.Length;

                    while (true)
                    {
                        if ((bin.BaseStream.Length - bin.BaseStream.Position) < chunkSize)
                        {
                            break;
                        }

                        var posInStack = Search(bin.ReadBytes(chunkSize), patternBytes);

                        if (posInStack != chunkSize)
                        {
                            var matchPos = bin.BaseStream.Position - chunkSize + posInStack;

                            Console.WriteLine("Pattern " + pattern.name + " matched at " + matchPos);

                            if (pattern.offsets.ContainsKey(Name.FDID))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.FDID];
                                if(bin.ReadUInt32() < 53183)
                                {
                                    continue;
                                }
                            }

                            if (pattern.offsets.ContainsKey(Name.RECORD_SIZE))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.RECORD_SIZE];
                                if (bin.ReadUInt32() == 0)
                                {
                                    continue;
                                }
                            }

                            if (pattern.offsets.ContainsKey(Name.DB_NAME))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.DB_NAME];
                                if (bin.ReadUInt32() < 10)
                                {
                                    continue;
                                }

                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.DB_NAME];
                                var targetOffset = (long)translate(bin.ReadUInt64());
                                if (targetOffset > bin.BaseStream.Length)
                                {
                                    continue;
                                }
                            }

                            if (pattern.offsets.ContainsKey(Name.DB_FILENAME))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.DB_FILENAME];
                                if (bin.ReadUInt32() < 10)
                                {
                                    continue;
                                }

                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.DB_FILENAME];
                                var targetOffset = (long)translate(bin.ReadUInt64());
                                if (targetOffset > bin.BaseStream.Length)
                                {
                                    continue;
                                }
                            }

                            if (pattern.offsets.ContainsKey(Name.NUM_FIELD_IN_FILE))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.NUM_FIELD_IN_FILE];
                                if (bin.ReadUInt32() > 5000)
                                {
                                    continue;
                                }

                            }
                            if (pattern.offsets.ContainsKey(Name.FIELD_TYPES_IN_FILE) && pattern.offsets.ContainsKey(Name.FIELD_SIZES_IN_FILE))
                            {
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.FIELD_TYPES_IN_FILE];
                                var fieldTypesInFile = bin.ReadInt64();
                                bin.BaseStream.Position = matchPos + pattern.offsets[Name.FIELD_SIZES_IN_FILE];
                                var fieldSizesInFileOffs = bin.ReadInt64();
                                if(fieldTypesInFile == fieldSizesInFileOffs)
                                {
                                    continue;
                                }
                            }

                            bin.BaseStream.Position = matchPos;
                            var meta = ReadMeta(bin, pattern);
                            bin.BaseStream.Position = (long)translate((ulong)meta.nameOffset);
                            metas.TryAdd(bin.ReadCString(), meta);
                            
                            bin.BaseStream.Position = matchPos + patternLength;
                        }
                        else
                        {
                            bin.BaseStream.Position = bin.BaseStream.Position - patternLength;
                        }
                    }

                    bin.BaseStream.Position = 0;
                }

                var outputDirectory = "definitions_" + build;

                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                // Process DBMetas
                foreach(var meta in metas)
                {
                    if((long)translate((ulong)meta.Value.field_offsets_offs) > bin.BaseStream.Length)
                    {
                        Console.WriteLine("Skipping reading of " + meta.Key + " because first offset is way out of range!");
                        continue;
                    }

                    var writer = new StreamWriter(Path.Combine(outputDirectory, meta.Key + ".dbd"));

                    writer.WriteLine("COLUMNS");

                    Console.Write("Writing " + meta.Key + ".dbd..");

                    var field_offsets = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_offsets_offs);

                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_offsets.Add(bin.ReadInt32());
                    }

                    var field_sizes = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_sizes_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_sizes.Add(bin.ReadInt32());
                    }

                    var field_types = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_types_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_types.Add(bin.ReadInt32());
                    }

                    var field_flags = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_flags_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_flags.Add(bin.ReadInt32());
                    }

                    var field_sizes_in_file = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_sizes_in_file_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_sizes_in_file.Add(bin.ReadInt32());
                    }

                    var field_types_in_file = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_types_in_file_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_types_in_file.Add(bin.ReadInt32());
                    }

                    // Read field flags in file
                    var field_flags_in_file = new List<int>();
                    bin.BaseStream.Position = (long)translate((ulong)meta.Value.field_flags_in_file_offs);
                    for (var i = 0; i < meta.Value.num_fields; i++)
                    {
                        field_flags_in_file.Add(bin.ReadInt32());
                    }

                    if (meta.Value.id_column == -1)
                    {
                        writer.WriteLine("int ID");
                    }

                    var columnNames = new List<string>();
                    var columnTypeFlags = new List<Tuple<int, int>>();

                    for(var i = 0; i < meta.Value.num_fields_in_file; i++)
                    {
                        columnTypeFlags.Add(new Tuple<int, int>(field_types_in_file[i], field_flags_in_file[i]));
                    }

                    if (meta.Value.num_fields_in_file != meta.Value.num_fields)
                    {
                        columnTypeFlags.Add(new Tuple<int, int>(field_types[meta.Value.num_fields_in_file], field_flags[meta.Value.num_fields_in_file]));
                    }

                    for(var i = 0; i < columnTypeFlags.Count; i++)
                    {
                        columnNames.Add("field_" + new Random().Next(1, int.MaxValue).ToString().PadLeft(9, '0'));

                        var t = TypeToT(columnTypeFlags[i].Item1, (FieldFlags)columnTypeFlags[i].Item2);
                        if(t.Item1 == "locstring")
                        {
                            writer.WriteLine(t.Item1 + " " + columnNames[i]+ "_lang");
                        }
                        else
                        {
                            writer.WriteLine(t.Item1 + " " + columnNames[i]);
                        }
                    }

                    writer.WriteLine();

                    writer.WriteLine("LAYOUT " + meta.Value.layout_hash.ToString("X8").ToUpper());
                    writer.WriteLine("BUILD " + build);

                    if(meta.Value.sparseTable == 1)
                    {
                        writer.WriteLine("COMMENT table is sparse");
                    }

                    if(meta.Value.id_column == -1)
                    {
                        writer.WriteLine("$noninline,id$ID<32>");
                    }

                    for (var i = 0; i < meta.Value.num_fields_in_file; i++)
                    {
                        var typeFlags = TypeToT(field_types_in_file[i], (FieldFlags)field_flags_in_file[i]);

                        if(meta.Value.id_column == i)
                        {
                            writer.Write("$id$");
                        }

                        if(build.StartsWith("7.3.5") || build.StartsWith("8.0.1"))
                        {
                            if (meta.Value.column_8C == i)
                            {
                                writer.Write("$relation$");
                                if (meta.Value.column_90 != i)
                                {
                                    throw new Exception("No column_90 but there is column_8C send help!");
                                }
                            }
                        }

                        writer.Write(columnNames[i]);

                        if(typeFlags.Item1 == "locstring")
                        {
                            writer.Write("_lang");
                        }

                        if(typeFlags.Item2 > 0)
                        {
                            writer.Write("<" + typeFlags.Item2 + ">");
                        }

                        if(field_sizes_in_file[i] != 1)
                        {
                            writer.Write("[" + field_sizes_in_file[i] + "]");
                        }

                        writer.WriteLine();
                    }

                    if (meta.Value.num_fields_in_file != meta.Value.num_fields)
                    {
                        var i = meta.Value.num_fields_in_file;
                        var typeFlags = TypeToT(field_types[i], (FieldFlags)field_flags[i]);

                        writer.Write("$noninline,relation$" + columnNames[i]);

                        if (typeFlags.Item1 == "locstring")
                        {
                            writer.Write("_lang");
                        }

                        if (typeFlags.Item2 > 0)
                        {
                            writer.Write("<" + typeFlags.Item2 + ">");
                        }

                        if (field_sizes[i] != 1)
                        {
                            writer.Write("[" + field_sizes[i] + "]");
                        }
                    }

                    writer.Flush();
                    writer.Close();

                    Console.Write("..done!\n");
                }
            }

            Environment.Exit(0);
        }

        private static DBMeta ReadMeta(BinaryReader bin, Pattern pattern)
        {
            var matchPos = bin.BaseStream.Position;

            var meta = new DBMeta();
            foreach(var offset in pattern.offsets)
            {
                bin.BaseStream.Position = matchPos + offset.Value;
                switch (offset.Key)
                {
                    case Name.DB_NAME:
                        meta.nameOffset = bin.ReadInt64();
                        break;
                    case Name.NUM_FIELD_IN_FILE:
                        meta.num_fields_in_file = bin.ReadInt32();
                        break;
                    case Name.RECORD_SIZE:
                        meta.record_size = bin.ReadInt32();
                        break;
                    case Name.NUM_FIELD:
                        meta.num_fields = bin.ReadInt32();
                        break;
                    case Name.ID_COLUMN:
                        meta.id_column = bin.ReadInt32();
                        break;
                    case Name.SPARSE_TABLE:
                        meta.sparseTable = bin.ReadByte();
                        break;
                    case Name.FIELD_OFFSETS:
                        meta.field_offsets_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_SIZES:
                        meta.field_sizes_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_TYPES:
                        meta.field_types_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_FLAGS:
                        meta.field_flags_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_SIZES_IN_FILE:
                        meta.field_sizes_in_file_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_TYPES_IN_FILE:
                        meta.field_types_in_file_offs = bin.ReadInt64();
                        break;
                    case Name.FIELD_FLAGS_IN_FILE:
                        meta.field_flags_in_file_offs = bin.ReadInt64();
                        break;
                    case Name.FLAGS_58_21:
                        meta.flags_58_2_1 = bin.ReadByte();
                        break;
                    case Name.TABLE_HASH:
                        meta.table_hash = bin.ReadInt32();
                        break;
                    case Name.LAYOUT_HASH:
                        meta.layout_hash = bin.ReadInt32();
                        break;
                    case Name.FLAGS_68_421:
                        meta.flags_68_4_2_1 = bin.ReadByte();
                        break;
                    case Name.FIELD_NUM_IDX_INT:
                        meta.nbUniqueIdxByInt = bin.ReadInt32();
                        break;
                    case Name.FIELD_NUM_IDX_STRING:
                        meta.nbUniqueIdxByString = bin.ReadInt32();
                        break;
                    case Name.FIELD_IDX_INT:
                        meta.uniqueIdxByInt = bin.ReadInt32();
                        break;
                    case Name.FIELD_IDX_STRING:
                        meta.uniqueIdxByString = bin.ReadInt32();
                        break;
                    case Name.UNK88:
                        meta.bool_88 = bin.ReadByte();
                        break;
                    case Name.FIELD_RELATION:
                        meta.column_8C = bin.ReadInt32();
                        break;
                    case Name.FIELD_RELATION_IN_FILE:
                        meta.column_90 = bin.ReadInt32();
                        break;
                    case Name.SORT_FUNC:
                        meta.sortFunctionOffs = bin.ReadInt64();
                        break;
                    case Name.UNKC0:
                        meta.bool_C0 = bin.ReadByte();
                        break;
                    case Name.FDID:
                        meta.fileDataID = bin.ReadInt32();
                        break;
                    case Name.DB_CACHE_FILENAME:
                        meta.cacheNameOffset = bin.ReadInt64();
                        break;
                    case Name.CONVERT_STRINGREFS:
                        meta.string_ref_offs = bin.ReadInt64();
                        break;
                    case Name.DB_FILENAME:
                        meta.dbFilename = bin.ReadInt64();
                        break;
                    case Name.FIELD_ENCRYPTED:
                        meta.field_encrypted = bin.ReadInt64();
                        break;
                    case Name.SQL_QUERY:
                        meta.sql_query = bin.ReadInt64();
                        break;
                    default:
                        throw new Exception("Unknown name: " + offset.Key);
                }
            }
            return meta;
        }

        public enum FieldFlags : int
        {
            f_maybe_fk = 1,
            f_maybe_compressed = 2, // according to simca
            f_unsigned = 4,
            f_localized = 8,
        };
        public static (string, int) TypeToT(int type, FieldFlags flag)
        {
            switch (type)
            {
                case 0:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                        case 0 | 0 | 0 | FieldFlags.f_maybe_fk:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | 0:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | FieldFlags.f_maybe_fk:
                            return ("int", 32);
                        case 0 | FieldFlags.f_unsigned | 0 | 0:
                        case 0 | FieldFlags.f_unsigned | 0 | FieldFlags.f_maybe_fk:
                        case 0 | FieldFlags.f_unsigned | FieldFlags.f_maybe_compressed | 0:
                        case 0 | FieldFlags.f_unsigned | FieldFlags.f_maybe_compressed | FieldFlags.f_maybe_fk:
                            return ("uint", 32);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                case 1:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                            return ("int", 64);
                        case 0 | FieldFlags.f_unsigned | 0 | 0:
                        case 0 | FieldFlags.f_unsigned | 0 | FieldFlags.f_maybe_fk:
                            return ("uint", 64);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                case 2:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                            return ("string", 0);
                        case FieldFlags.f_localized | 0 | 0 | 0:
                            return ("locstring", 0);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                case 3:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | 0:
                            return ("float", 0);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                case 4:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | 0:
                            return ("int", 8);
                        case 0 | FieldFlags.f_unsigned | 0 | 0:
                        case 0 | FieldFlags.f_unsigned | FieldFlags.f_maybe_compressed | 0:
                            return ("uint", 8);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                case 5:
                    switch (flag)
                    {
                        case 0 | 0 | 0 | 0:
                        case 0 | 0 | 0 | FieldFlags.f_maybe_fk:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | 0:
                        case 0 | 0 | FieldFlags.f_maybe_compressed | FieldFlags.f_maybe_fk:
                            return ("int", 16);
                        case 0 | FieldFlags.f_unsigned | 0 | 0:
                        case 0 | FieldFlags.f_unsigned | 0 | FieldFlags.f_maybe_fk:
                        case 0 | FieldFlags.f_unsigned | FieldFlags.f_maybe_compressed | 0:
                        case 0 | FieldFlags.f_unsigned | FieldFlags.f_maybe_compressed | FieldFlags.f_maybe_fk:
                            return ("uint", 16);
                        default:
                            throw new Exception("Unknown flag combination!");
                    }
                default:
                    throw new Exception("Ran into unknown field type: " + type);
            }
        }
        public static int Search(byte[] haystack, byte?[] needle)
        {
            int first = 0;
            for (; ; ++first)
            {
                int it = first;
                for (int s_it = 0; ; ++it, ++s_it)
                {
                    if (s_it == needle.Length)
                    {
                        return first;
                    }
                    if (it == haystack.Length)
                    {
                        return haystack.Length;
                    }
                    if (needle[s_it].HasValue && haystack[it] != needle[s_it].Value)
                    {
                        break;
                    }
                }
            }
        }
        public static List<byte?> ParsePattern(string pattern)
        {
            // Parse pattern
            var explodedPattern = pattern.Split(' ');

            var patternList = new List<byte?>();
            foreach (var part in explodedPattern)
            {
                if (part == string.Empty)
                {
                    continue;
                }

                if (part != "?")
                {
                    patternList.Add(Convert.ToByte(part));
                }
                else
                {
                    patternList.Add(null);
                }
            }

            return patternList;
        }
    }
    
    #region BinaryReaderExtensions
    static class BinaryReaderExtensios
    {
        /// <summary> Reads the NULL terminated string from 
        /// the current stream and advances the current position of the stream by string length + 1.
        /// <seealso cref="BinaryReader.ReadString"/>
        /// </summary>
        public static string ReadCString(this BinaryReader reader)
        {
            return reader.ReadCString(Encoding.UTF8);
        }

        /// <summary> Reads the NULL terminated string from 
        /// the current stream and advances the current position of the stream by string length + 1.
        /// <seealso cref="BinaryReader.ReadString"/>
        /// </summary>
        public static string ReadCString(this BinaryReader reader, Encoding encoding)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                bytes.Add(b);
            return encoding.GetString(bytes.ToArray());
        }

        public static T Read<T>(this BinaryReader bin)
        {
            var bytes = bin.ReadBytes(Marshal.SizeOf(typeof(T)));
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            T ret = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            handle.Free();
            return ret;
        }
    }
    #endregion
}
