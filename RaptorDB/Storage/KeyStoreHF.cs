﻿using RaptorDB.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RaptorDB
{
    internal class AllocationBlock
    {
        public string key;
        public byte keylen;
        public int datalength;
        public bool isCompressed;
        public bool isBinaryJSON;
        public bool deleteKey;
        public List<int> Blocks = new List<int>();
        public int blocknumber;
    }

    public interface IKeyStoreHF
    {
        /// <summary>
        /// Get the object for the key
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        object GetObject(string key);
        /// <summary>
        /// Set the object for the key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        bool SetObject(string key, object obj);
        /// <summary>
        /// Delete key 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        bool DeleteKey(string key);
        /// <summary>
        /// Count the number of items
        /// </summary>
        /// <returns></returns>
        int Count();
        /// <summary>
        /// If the key exists
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        bool Contains(string key);
        //IEnumerable<object> EnumerateObjects();
        /// <summary>
        /// Get the keys as an array
        /// </summary>
        /// <returns></returns>
        string[] GetKeys();
        // SearchKeys()
    }

    internal class KeyStoreHF : IKeyStoreHF
    {
        MGIndex<string> _keys;
        StorageFileHF _datastore;
        object _lock = new object();
        ushort _BlockSize = 2048; // FEATURE : bring out as a config
        private const int _KILOBYTE = 1024;
        ILog _log = LogManager.GetLogger(typeof(KeyStoreHF));

        byte[] _blockheader = new byte[]{
            0,0,0,0,    // 0  block # (used for validate block reads and rebuild)
            0,0,0,0,    // 4  next block # 
            0,          // 8  flags bits 0:iscompressed  1:isbinary  2:deletekey
            0,0,0,0,    // 9  data length (compute alloc blocks needed)
            0,          // 13 key length 
            0,          // 14 key type 0=guid 1=string
        };
        private string _Path = "";
        private string _S = Path.DirectorySeparatorChar.ToString();
        private bool _isDirty = false;
        private string _dirtyFilename = "temp.$";

        // high frequency key value store

        public KeyStoreHF(string folder)
        {
            _Path = folder;
            Directory.CreateDirectory(_Path);
            if (_Path.EndsWith(_S) == false) _Path += _S;

            if (File.Exists(_Path + "temp.$"))
            {
                _log.Error("Last shutdown failed, rebuilding data files...");
                RebuildDataFiles();
            }
            _datastore = new StorageFileHF(_Path + "data.mghf", _BlockSize);
            _keys = new MGIndex<string>(_Path, "keys.idx", 255, Global.PageItemCount, false);

            _BlockSize = _datastore.GetBlockSize();
        }

        public int Count()
        {
            return _keys.Count();
        }

        public object GetObject(string key)
        {
            lock (_lock)
            {
                int alloc;
                if (_keys.Get(key, out alloc))
                {
                    AllocationBlock ab = FillAllocationBlock(alloc);
                    if (ab.deleteKey == false)
                    {
                        byte[] data = new byte[ab.datalength];
                        long offset = 0;
                        int len = ab.datalength;
                        int dbsize = _BlockSize - _blockheader.Length - ab.keylen;
                        ab.Blocks.ForEach(x =>
                        {
                            byte[] b = _datastore.ReadBlock(x);
                            int c = len;
                            if (c > dbsize) c = dbsize;
                            Buffer.BlockCopy(b, _blockheader.Length + ab.keylen, data, (int)offset, c);
                            offset += c;
                            len -= c;
                        });
                        if (ab.isCompressed)
                            data = MiniLZO.Decompress(data);

                        return fastBinaryJSON.BJSON.ToObject(data);
                    }
                }
            }

            return null;
        }

        public bool SetObject(string key, object obj)
        {
            byte[] k = Helper.GetBytes(key);
            if (k.Length > 255)
            {
                _log.Error("Key length > 255 : " + key);
                throw new Exception("Key must be less than 255 characters");
                //return false;
            }
            lock (_lock)
            {
                if (_isDirty == false)
                    WriteDirtyFile();

                AllocationBlock ab = null;
                int firstblock = 0;
                if (_keys.Get(key, out firstblock))// key exists already
                    ab = FillAllocationBlock(firstblock);

                SaveNew(key, k, obj);
                if (ab != null)
                {
                    // free old blocks
                    ab.Blocks.Add(ab.blocknumber);
                    _datastore.FreeBlocks(ab.Blocks);
                }
                return true;
            }
        }

        public bool DeleteKey(string key)
        {
            lock (_lock)
            {
                int alloc;
                if (_keys.Get(key, out alloc))
                {
                    if (_isDirty == false)
                        WriteDirtyFile();

                    byte[] keybytes = Helper.GetBytes(key);
                    AllocationBlock ab = FillAllocationBlock(alloc);

                    ab.keylen = (byte)keybytes.Length;

                    // remove key from index
                    _keys.RemoveKey(key);

                    // write ab
                    ab.deleteKey = true;
                    ab.datalength = 0;
                    ab.blocknumber = _datastore.GetFreeBlockNumber();

                    byte[] header = CreateAllocHeader(ab, keybytes);

                    _datastore.SeekBlock(ab.blocknumber);
                    _datastore.WriteBlockBytes(header, 0, header.Length);
                    _datastore.WriteBlockBytes(new byte[_BlockSize - header.Length], 0, _BlockSize - header.Length);

                    // free old data blocks
                    _datastore.FreeBlocks(ab.Blocks);

                    return true;
                }
            }
            return false;
        }

        //public void CompactStorage()
        //{
        //    // FEATURE : compact storage file
        //    throw new NotImplementedException();
        //}

        //public IEnumerable<object> EnumerateObjects()
        //{
        //    foreach (var k in _keys.GetKeys())
        //    {
        //        yield return GetObject(k.ToString());
        //    }
        //}

        public string[] GetKeys()
        {
            return _keys.GetKeys().Cast<string>().ToArray(); // FEATURE : dirty !?
        }

        public bool Contains(string key)
        {
            int i = 0;
            return _keys.Get(key, out i);
        }

        internal void Shutdown()
        {
            _datastore.Shutdown();
            // _keys.SaveIndex();
            _keys.Shutdown();

            if (File.Exists(_Path + _dirtyFilename))
                File.Delete(_Path + _dirtyFilename);
        }

        internal void FreeMemory()
        {
            _keys.FreeMemory();
        }

        #region [  private methods  ]
        private object _dfile = new object();
        private void WriteDirtyFile()
        {
            lock (_dfile)
            {
                _isDirty = true;
                if (File.Exists(_Path + _dirtyFilename) == false)
                    File.WriteAllText(_Path + _dirtyFilename, "dirty");
            }
        }

        private void SaveNew(string key, byte[] keybytes, object obj)
        {
            byte[] data;
            AllocationBlock ab = new AllocationBlock();
            ab.key = key;
            ab.keylen = (byte)keybytes.Length;

            data = fastBinaryJSON.BJSON.ToBJSON(obj);
            ab.isBinaryJSON = true;

            if (data.Length > (int)Global.CompressDocumentOverKiloBytes * _KILOBYTE)
            {
                ab.isCompressed = true;
                data = MiniLZO.Compress(data);
            }
            ab.datalength = data.Length;

            int firstblock = _datastore.GetFreeBlockNumber();
            int blocknum = firstblock;
            byte[] header = CreateAllocHeader(ab, keybytes);
            int dblocksize = _BlockSize - header.Length;
            int offset = 0;
            // compute data block count
            int datablockcount = (data.Length / dblocksize) + 1;
            // save data blocks
            int counter = 0;
            int len = data.Length;
            while (datablockcount > 0)
            {
                datablockcount--;
                int next = 0;
                if (datablockcount > 0)
                    next = _datastore.GetFreeBlockNumber();

                Buffer.BlockCopy(Helper.GetBytes(counter, false), 0, header, 0, 4);    // set block number
                Buffer.BlockCopy(Helper.GetBytes(next, false), 0, header, 4, 4); // set next pointer

                _datastore.SeekBlock(blocknum);
                _datastore.WriteBlockBytes(header, 0, header.Length);
                int c = len;
                if (c > dblocksize)
                    c = dblocksize;
                _datastore.WriteBlockBytes(data, offset, c);

                if (next > 0)
                    blocknum = next;
                offset += c;
                len -= c;
                counter++;
            }

            // save keys
            _keys.Set(key, firstblock);
        }

        private byte[] CreateAllocHeader(AllocationBlock ab, byte[] keybytes)
        {
            byte[] alloc = new byte[_blockheader.Length + keybytes.Length];

            if (ab.isCompressed)
                alloc[8] = 1;
            if (ab.isBinaryJSON)
                alloc[8] += 2;
            if (ab.deleteKey)
                alloc[8] += 4;

            Buffer.BlockCopy(Helper.GetBytes(ab.datalength, false), 0, alloc, 9, 4);
            alloc[13] = ab.keylen;
            alloc[14] = 1; // string keys for now
            Buffer.BlockCopy(keybytes, 0, alloc, _blockheader.Length, ab.keylen);

            return alloc;
        }

        private AllocationBlock FillAllocationBlock(int blocknumber)
        {
            AllocationBlock ab = new AllocationBlock();

            ab.blocknumber = blocknumber;
            ab.Blocks.Add(blocknumber);

            byte[] b = _datastore.ReadBlockBytes(blocknumber, _blockheader.Length + 255);

            int blocknumexpected = 0;

            int next = ParseBlockHeader(ab, b, blocknumexpected);

            blocknumexpected++;

            while (next > 0)
            {
                ab.Blocks.Add(next);
                b = _datastore.ReadBlockBytes(next, _blockheader.Length + ab.keylen);
                next = ParseBlockHeader(ab, b, blocknumexpected);
                blocknumexpected++;
            }

            return ab;
        }

        private int ParseBlockHeader(AllocationBlock ab, byte[] b, int blocknumberexpected)
        {
            int bnum = Helper.ToInt32(b, 0);
            if (bnum != blocknumberexpected)
            {
                _log.Error("Block numbers does not match, looking for : " + blocknumberexpected);
                //throw new Exception("Block numbers does not match, looking for : " + blocknumberexpected);
                return -1;
            }
            if (b[14] != 1)
            {
                _log.Error("Expecting string keys only, got : " + b[14]);
                //throw new Exception("Expecting string keys only, got : " + b[11]);
                return -1;
            }

            int next = Helper.ToInt32(b, 4);

            if (ab.keylen == 0)
            {
                byte flags = b[8];

                if ((flags & 0x01) > 0)
                    ab.isCompressed = true;
                if ((flags & 0x02) > 0)
                    ab.isBinaryJSON = true;
                if ((flags & 0x04) > 0)
                    ab.deleteKey = true;

                ab.datalength = Helper.ToInt32(b, 9);
                byte keylen = b[13];
                ab.keylen = keylen;
                ab.key = Helper.GetString(b, _blockheader.Length, keylen);
            }
            return next;
        }

        private void RebuildDataFiles()
        {
            MGIndex<string> keys = null;
            try
            {
                // remove old free list
                if (File.Exists(_Path + "data.bmp"))
                    File.Delete(_Path + "data.bmp");

                _datastore = new StorageFileHF(_Path + "data.mghf", _BlockSize);
                _BlockSize = _datastore.GetBlockSize();
                if (File.Exists(_Path + "keys.idx"))
                {
                    _log.Debug("removing old keys index");
                    File.Delete(_Path + "keys.idx");
                }

                keys = new MGIndex<string>(_Path, "keys.idx", 255, Global.PageItemCount, false);

                WAHBitArray visited = new WAHBitArray();

                int c = _datastore.NumberofBlocks();

                for (int i = 0; i < c; i++) // go through blocks
                {
                    if (visited.Get(i))
                        continue;
                    byte[] b = _datastore.ReadBlockBytes(i, _blockheader.Length + 255);
                    int bnum = Helper.ToInt32(b, 0);
                    if (bnum > 0) // check if a start block
                    {
                        visited.Set(i, true);
                        _datastore.FreeBlock(i); // mark as free
                        continue;
                    }

                    AllocationBlock ab = new AllocationBlock();
                    // start block found
                    int blocknumexpected = 0;

                    int next = ParseBlockHeader(ab, b, blocknumexpected);
                    int last = 0;
                    bool freelast = false;
                    AllocationBlock old = null;

                    if (keys.Get(ab.key, out last))
                    {
                        old = this.FillAllocationBlock(last);
                        freelast = true;
                    }
                    blocknumexpected++;
                    bool failed = false;
                    if (ab.deleteKey == false)
                    {
                        while (next > 0) // read the blocks
                        {
                            ab.Blocks.Add(next);
                            b = _datastore.ReadBlockBytes(next, _blockheader.Length + ab.keylen);
                            next = ParseBlockHeader(ab, b, blocknumexpected);
                            if (next == -1) // non matching block
                            {
                                failed = true;
                                break;
                            }
                            blocknumexpected++;
                        }
                    }
                    else
                    {
                        failed = true;
                        keys.RemoveKey(ab.key);
                    }
                    // new data ok
                    if (failed == false)
                    {
                        // valid block found
                        keys.Set(ab.key, ab.blocknumber);
                        // free the old blocks
                        if (freelast)
                            _datastore.FreeBlocks(old.Blocks);
                    }

                    visited.Set(i, true);
                }

                // all ok delete temp.$ file
                if (File.Exists(_Path + _dirtyFilename))
                    File.Delete(_Path + _dirtyFilename);
            }
            catch (Exception ex)
            {
                _log.Error(ex);
            }
            finally
            {
                _log.Debug("Shutting down files and index");
                _datastore.Shutdown();
                keys.SaveIndex();
                keys.Shutdown();
            }
        }
        #endregion
    }
}
