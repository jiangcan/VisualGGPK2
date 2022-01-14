﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using LibDat2.Types;

namespace LibDat2 {
	public class DatContainer {
		/// <summary>
		/// Structure definition of dat files
		/// </summary>
		public static Dictionary<string, KeyValuePair<string, string>[]> DatDefinitions;

		/// <summary>
		/// DatDefinitions from schema.min.json
		/// </summary>
		public static Dictionary<string, KeyValuePair<string, string>[]>? SchemaMinDatDefinitions;

		/// <summary>
		/// Whether the current using DatDefinitions is from schema.min.json
		/// </summary>
		public readonly bool SchemaMin;

		/// <summary>
		/// Call <see cref="ReloadDefinitions"/>
		/// </summary>
#pragma warning disable CS8618
		static DatContainer() {
			ReloadDefinitions();
		}

		public static void DownloadSchemaMin() {
			var http = new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
			try {
				http.DefaultRequestHeaders.Add("User-Agent", "LibDat2");
				var s = http.GetStringAsync("http://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json").Result;
				var json = JsonDocument.Parse(s);
				var table = json.RootElement.GetProperty("tables");
				SchemaMinDatDefinitions = new(table.GetArrayLength());
				foreach (var dat in table.EnumerateArray()) {
					var columns = dat.GetProperty("columns");
					var array = new KeyValuePair<string, string>[columns.GetArrayLength()];
					var Unknown = 0;
					var index = 0;
					foreach (var field in columns.EnumerateArray()) {
						var name = field.GetProperty("name").GetString() ?? "Unknown" + Unknown++.ToString();
						var type = field.GetProperty("type").GetString()!;
						if (field.GetProperty("array").GetBoolean())
							type = "array|" + type;
						array[index++] = new(name, type);
					}
					SchemaMinDatDefinitions.Add(dat.GetProperty("name").GetString()!, array);
				}
				json.Dispose();
			} finally {
				http.Dispose();
			}
		}

		/// <summary>
		/// Definition of fields in this dat
		/// Left: Name of field, Right: Type of field data
		/// </summary>
		public readonly ReadOnlyCollection<KeyValuePair<string, string>> FieldDefinitions;

		/// <summary>
		/// Name of the dat file
		/// </summary>
		public readonly string Name;

		/// <summary>
		/// List of record content of the dat file
		/// </summary>
		public List<IFieldData?[]?> FieldDatas;

		/// <summary>
		/// Store the first error that occurred during reading
		/// </summary>
		public readonly DatDataReadException? Exception;

		/// <summary>
		/// Used to dispose the FileStream created when calling <see cref="DatContainer(string)"/>
		/// </summary>
		private static FileStream? tmp;
		/// <summary>
		/// Parses the dat file contents from a file
		/// </summary>
		/// <param name="filePath">Path of a dat file</param>
		/// <param name="SchemaMin">Whether to use schema.min.json</param>
		public DatContainer(string filePath, bool SchemaMin = false) : this(tmp = File.OpenRead(filePath), filePath, SchemaMin) {
			tmp.Close();
			tmp = null;
		}

		/// <summary>
		/// Parses the dat file contents from a binary data
		/// </summary>
		/// <param name="fileData">Binary data of a dat file</param>
		/// <param name="SchemaMin">Whether to use schema.min.json</param>
		public DatContainer(byte[] fileData, string fileName, bool SchemaMin = false) : this(new MemoryStream(fileData), fileName, SchemaMin) { }

		/// <summary>
		/// Parses the dat file contents from a stream
		/// </summary>
		/// <param name="stream">Contents of a dat file</param>
		/// <param name="fileName">Name of the dat file</param>
		/// <param name="SchemaMin">Whether to use schema.min.json</param>
		public DatContainer(Stream stream, string fileName, bool SchemaMin = false) {
			this.SchemaMin = SchemaMin;
			switch (Path.GetExtension(fileName)) {
				case ".dat":
					x64 = false;
					UTF32 = false;
					break;
				case ".dat64":
					x64 = true;
					UTF32 = false;
					break;
				case ".datl":
					x64 = false;
					UTF32 = true;
					break;
				case ".datl64":
					x64 = true;
					UTF32 = true;
					break;
				default:
					throw new ArgumentException("The provided file name must be a dat file", nameof(fileName));
			}

			Name = Path.GetFileNameWithoutExtension(fileName);
			var reader = new BinaryReader(stream, UTF32 ? Encoding.UTF32 : Encoding.Unicode);
			var Count = reader.ReadInt32();
			string def;

			if (SchemaMin) {
				def = "schema.min.json";
				if (SchemaMinDatDefinitions == null)
					DownloadSchemaMin();
				if (!SchemaMinDatDefinitions!.TryGetValue(Name, out var kvps))
					throw new KeyNotFoundException(Name + " was not defined in " + def);
				FieldDefinitions = new(kvps);
			} else {
				def = "DatDefinitions";
				if (!DatDefinitions.TryGetValue(Name, out var kvps))
					throw new KeyNotFoundException(Name + " was not defined in " + def);
				FieldDefinitions = new(kvps);
			}

			if (Name != "Languages") {
				var actualRecordLength = GetActualRecordLength(reader, Count);
				DataSectionOffset = Count * actualRecordLength + 4;
				// DataSectionDataLength = Length - DataSectionOffset - 8;

				var recordLength = CalculateRecordLength(FieldDefinitions.Select(t => t.Value), x64);
				if (recordLength != actualRecordLength)
					throw new($"{fileName} : Actual record length: {actualRecordLength} is not equal to that defined in {def}: {recordLength}");

				reader.BaseStream.Seek(4, SeekOrigin.Begin);
			}

			Exception = Read(reader, Count);
		}

		protected virtual DatDataReadException? Read(BinaryReader reader, int entryCount) {
			ReferenceDatas.Clear();
			ReferenceDataOffsets.Clear();
			DatDataReadException? ex = null;
			FieldDatas = new(entryCount);
			var lastPos = reader.BaseStream.Position;
			for (var i = 0; i < entryCount; ++i) {
				if (ex != null) {
					FieldDatas.Add(null);
					continue;
				}
				var row = new IFieldData[FieldDefinitions.Count];
				var index = 0;
				foreach (var type in FieldDefinitions.Select(t => t.Value)) {
					try {
						if (type.StartsWith("array|"))
							row[index++] = IArrayData.Read(reader, IFieldData.TypeFromString[type[6..]], this);
						else
							row[index++] = IFieldData.Read(reader, IFieldData.TypeFromString[type], this);
						lastPos = reader.BaseStream.Position;
					} catch (Exception e) {
						ex = new(Name, i, index - 1, FieldDefinitions[index - 1].Key, reader.BaseStream.Position, lastPos, e);
						break;
					}
				}
				FieldDatas.Add(row);
			}

			if (ReferenceDatas.Count != 0)
				CurrentOffset = ReferenceDatas.Values.ElementAt(ReferenceDatas.Count - 1).EndOffset;
			reader.BaseStream.Seek(DataSectionOffset + CurrentOffset, SeekOrigin.Begin); // Move to the end of dat file
			return ex;
		}

		/*
		/// <summary>
		/// Convert type from Old Definitions before v0.11.4 to the new format
		/// </summary>
		public static string ToNewType(string type) {
			if (type == "ref|foreignkey")
				return "foreignrow";
			if (type == "ref|string")
				return "string";
			if (type.StartsWith("ref|list|"))
				return "array|" + ToNewType(type[9..]);
			if (type.StartsWith("ref|"))
				return "row";
			return type switch {
				"bool" => "bool",
				"byte" => "i8",
				"short" => "i16",
				"ushort" => "u16",
				"int" => "i32",
				"uint" => "u32",
				"long" => "i64",
				"ulong" => "u64",
				"float" => "f32",
				"double" => "f64",
				"string" => "valueString",
				_ => throw new InvalidCastException("Unknown type: " + type);
			};
		}
		*/

		// Dependents on the last read/saved dat file
		#region FileDependent
		/// <summary>
		/// Set of IReferenceData of the dat file last read/save
		/// </summary>
		public readonly SortedDictionary<long, IReferenceData> ReferenceDatas = new();
		/// <summary>
		/// Used to find IReferenceData with the actual data in string representation
		/// </summary>
		protected internal readonly Dictionary<string, long> ReferenceDataOffsets = new();
		/// <summary>
		/// Whether the pointer length is 64 bits, otherwise is 32 bits
		/// </summary>
		public bool x64;
		/// <summary>
		/// Whether the strings is save as UTF-32, otherwise is UTF-16
		/// </summary>
		public bool UTF32;
		/// <summary>
		/// The begin offset of DataSection(Which contains pointed values and starts with 0xBBBBBBBBBBBBBBBB)
		/// </summary>
		public long DataSectionOffset;
		/// <summary>
		/// Temporary record the offset in DataSection while writing to a dat file
		/// </summary>
		protected internal long CurrentOffset = 8;
		#endregion

		/// <summary>
		/// Create a DatContainer with Datas
		/// </summary>
		/// <param name="fieldDatas">Contents of a dat file</param>
		/// <param name="fileName">Name of the dat file</param>
		/// <param name="SchemaMin">Whether to use schema.min.json</param>
		public DatContainer(string fileName, List<IFieldData?[]?>? fieldDatas = null, bool SchemaMin = false) {
			this.SchemaMin = SchemaMin;
			switch (Path.GetExtension(fileName)) {
				case ".dat":
					x64 = false;
					UTF32 = false;
					break;
				case ".dat64":
					x64 = true;
					UTF32 = false;
					break;
				case ".datl":
					x64 = false;
					UTF32 = true;
					break;
				case ".datl64":
					x64 = true;
					UTF32 = true;
					break;
				default:
					throw new ArgumentException("The provided file name must be a dat file", nameof(fileName));
			}

			Name = Path.GetFileNameWithoutExtension(fileName);

			if (SchemaMin) {
				if (SchemaMinDatDefinitions == null)
					DownloadSchemaMin();
				if (!SchemaMinDatDefinitions!.TryGetValue(Name, out var kvps))
					throw new KeyNotFoundException(Name + " was not defined in schema.min.json");
				FieldDefinitions = new(kvps);
			} else {
				if (!DatDefinitions.TryGetValue(Name, out var kvps))
					throw new KeyNotFoundException(Name + " was not defined in DatDefinitions");
				FieldDefinitions = new(kvps);
			}

			FieldDatas = fieldDatas ?? new();
		}

		/// <summary>
		/// Save the dat file with the modified <see cref="FieldDatas"/>
		/// </summary>
		public virtual void Save(string filePath, bool x64, bool UTF32) {
			var f = File.Create(filePath);
			Save(f, x64, UTF32);
			f.Close();
		}

		/// <summary>
		/// Save the dat file with the modified <see cref="FieldDatas"/>
		/// </summary>
		public virtual byte[] Save(bool x64, bool UTF32) {
			var f = new MemoryStream();
			Save(f, x64, UTF32);
			var b = f.ToArray();
			f.Close();
			return b;
		}

		/// <summary>
		/// Save the dat file with the modified <see cref="FieldDatas"/>
		/// The position of the stream must be 0
		/// </summary>
		protected virtual void Save(Stream stream, bool x64, bool UTF32) {
			var bw = new BinaryWriter(stream);
			bw.Write(FieldDatas.Count);
			this.x64 = x64;
			this.UTF32 = UTF32;
			CurrentOffset = 8;
			DataSectionOffset = FieldDatas.Count * CalculateRecordLength(FieldDefinitions.Select(t => t.Value), x64) + 4;
			ReferenceDatas.Clear();
			ReferenceDataOffsets.Clear();
			foreach (var fds in FieldDatas)
				foreach (var fd in fds!)
					fd!.Write(bw);
			bw.Write(0xBBBBBBBBBBBBBBBB); // Magic number
			bw.Seek((int)(DataSectionOffset + CurrentOffset), SeekOrigin.Begin); // Move to the end of dat file
		}

		/// <summary>
		/// Convert <see cref="FieldDatas"/> to csv format
		/// </summary>
		/// <returns>Content of the csv file</returns>
		public virtual string ToCsv() {
			var f = new StringBuilder();
			var reg = new Regex("\n|\r|,", RegexOptions.Compiled);

			// Field Names
			foreach (var field in FieldDefinitions.Select(t => t.Key))
				if (field.StartsWith('"') || reg.IsMatch(field))
					f.Append("\"" + field.Replace("\"", "\"\"") + "\",");
				else
					f.Append(field + ",");

			if (f.Length == 0) {
				for (var i=0; i< FieldDatas.Count; ++i)
					f.AppendLine();
				return f.ToString();
			} else
				f.Length -= 1; // Remove ,
			f.AppendLine();

			foreach (var row in FieldDatas) {
				foreach (var col in row!) {
					var s = col!.ToString();
					if (s.StartsWith('"') || reg.IsMatch(s))
						f.Append("\"" + s.Replace("\"", "\"\"") + "\",");
					else
						f.Append(s + ",");
				}
				f.Length -= 1; // Remove ,
				f.AppendLine();
			}
			f.Length -= 1; // Remove ,

			return f.ToString();
		}

		/// <summary>
		/// Read <see cref="FieldDatas"/> from content of a csv file
		/// </summary>
		public virtual void FromCsv(string csv) {
			var sr = new StringReader(csv.Replace("\r\n", "\n"));
			if (sr.ReadLine() != string.Join(',', FieldDefinitions.Select(kvp => kvp.Key)))
				throw new("Field names unmatched");

			CurrentOffset = 8;
			ReferenceDataOffsets.Clear();
			ReferenceDatas.Clear();

			var quotes = false;
			var row = new IFieldData[FieldDefinitions.Count];
			var i = 0;
			var s = new StringBuilder();
			var list = new List<IFieldData?[]?>(FieldDatas.Count);

			if (sr.Peek() == '"') {
				sr.Read();
				quotes = true;
			}
			for (var chr = sr.Read(); chr != -1; chr = sr.Read())
				switch (chr) {
					case '"':
						if (quotes) {
							if (sr.Peek() == '"') {
								sr.Read();
								goto default;
							}
							quotes = false;
							break;
						}
						goto default;
					case ',':
						if (quotes)
							goto default;
						if (sr.Peek() == '"') {
							sr.Read();
							quotes = true;
						}
						var type = FieldDefinitions[i].Value;
						if (type.StartsWith("array|"))
							row[i++] = IArrayData.FromString(s.ToString(), IFieldData.TypeFromString[type[6..]], this);
						else
							row[i++] = IFieldData.FromString(s.ToString(), IFieldData.TypeFromString[type], this);
						s.Length = 0;
						break;
					case '\r':
					case '\n':
						if (quotes)
							goto default;
						if (sr.Peek() == '"') {
							sr.Read();
							quotes = true;
						}
						var type2 = FieldDefinitions[i].Value;
						if (type2.StartsWith("array|"))
							row[i] = IArrayData.FromString(s.ToString(), IFieldData.TypeFromString[type2[6..]], this);
						else
							row[i] = IFieldData.FromString(s.ToString(), IFieldData.TypeFromString[type2], this);
						i = 0;
						s.Length = 0;
						list.Add(row);
						row = new IFieldData[row.Length];
						break;
					case -1:
						break;
					default:
						s.Append(char.ConvertFromUtf32(chr));
						break;
				}

			FieldDatas = list;
		}

		/// <summary>
		/// Get the length of records in the dat file
		/// </summary>
		protected static long GetActualRecordLength(BinaryReader reader, int entryCount) {
			if (entryCount == 0)
				return 0;
			for (long i = 0, offset = reader.BaseStream.Position; reader.BaseStream.Position - offset <= reader.BaseStream.Length - 8; ++i) {
				var ul = reader.ReadUInt64();
				if (ul == 0xBBBBBBBBBBBBBBBB) // Magic number
					return i;
				reader.BaseStream.Seek(-8 + entryCount, SeekOrigin.Current);
			}
			throw new DatReadException("Missing magic number after records");
		}

		/// <summary>
		/// Calculate the expected length of records in the dat file
		/// </summary>
		protected static long CalculateRecordLength(IEnumerable<string> fields, bool x64) {
			long result = 0;
			foreach (var type in fields)
				result += FieldTypeLength(type, x64);
			return result;
		}

		/// <summary>
		/// Get the length in dat file of a type of field
		/// </summary>
		public static int FieldTypeLength(string type, bool x64) {
			if (type.StartsWith("array|"))
				return x64 ? 16 : 8;
			else
				return type switch {
					"foreignrow" => x64 ? 16 : 8,
					"row" => x64 ? 8 : 4,
					"string" => x64 ? 8 : 4,
					"bool" => 1,
					"i8" => 1,
					"u8" => 1,
					"i16" => 2,
					"u16" => 2,
					"i32" => 4,
					"u32" => 4,
					"f32" => 4,
					"i64" => 8,
					"u64" => 8,
					"f64" => 8,
					"valueString" => 0,
					"array" => 0,
					_ => throw new InvalidCastException($"Unknown Type: {type}")
				};
		}

		/// <summary>
		/// Reload DatDefinitions from a file
		/// This won't affect the existing DatContainers
		/// </summary>
		public static void ReloadDefinitions(string filePath = "DatDefinitions.json") {
			var bp = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
			if (bp is not null)
				filePath = Path.GetFullPath(filePath, bp);
			var json = JsonDocument.Parse(File.ReadAllBytes(filePath), new() { CommentHandling = JsonCommentHandling.Skip });
			try {
				DatDefinitions = new();
				foreach (var dat in json.RootElement.EnumerateObject())
					DatDefinitions.Add(dat.Name, dat.Value.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)).ToArray());
			} finally {
				json.Dispose();
			}
		}
	}
}