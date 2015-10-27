﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using BizHawk.Emulation.Common;

//TODO - reorg please
using BizHawk.Emulation.Cores.Nintendo.Gameboy;

namespace BizHawk.Emulation.Cores.Components.H6280
{
	public class CodeDataLog_PCE : CodeDataLog
	{
		public static CodeDataLog_PCE Create(IEnumerable<HuC6280.MemMapping> mm)
		{
			var t = new CodeDataLog_PCE();
			foreach (var kvp in SizesFromHuMap(mm))
			{
				t[kvp.Key] = new byte[kvp.Value];
			}
			return t;
		}

		public override string SubType { get { return "PCE"; } }
		public override int SubVer { get { return 0; } }

		public override void Disassemble(Stream s, IMemoryDomains mem)
		{
			var w = new StreamWriter(s);
			w.WriteLine("; Bizhawk CDL Disassembly");
			w.WriteLine();
			foreach (var kvp in this)
			{
				w.WriteLine(".\"{0}\" size=0x{1:x8}", kvp.Key, kvp.Value.Length);

				byte[] cd = kvp.Value;
				var md = mem[kvp.Key];

				for (int i = 0; i < kvp.Value.Length; i++)
				{
					if ((kvp.Value[i] & (byte)HuC6280.CDLUsage.Code) != 0)
					{
						int unused;
						string dis = HuC6280.DisassembleExt(
							0,
							out unused,
							delegate(ushort addr)
							{
								return md.PeekByte(addr + i);
							},
							delegate(ushort addr)
							{
								return md.PeekWord(addr + i, false);
							}
						);
						w.WriteLine("0x{0:x8}: {1}", i, dis);
					}
				}
				w.WriteLine();
			}
			w.WriteLine("; EOF");
			w.Flush();
		}

		public bool CheckConsistency(object arg)
		{
			var mm = (IEnumerable<HuC6280.MemMapping>)arg;
			var sizes = SizesFromHuMap(mm);
			if (sizes.Count != Count)
				return false;
			foreach (var kvp in sizes)
			{
				if (!ContainsKey(kvp.Key))
					return false;
				if (this[kvp.Key].Length != kvp.Value)
					return false;
			}
			return true;
		}

		private static Dictionary<string, int> SizesFromHuMap(IEnumerable<HuC6280.MemMapping> mm)
		{
			Dictionary<string, int> sizes = new Dictionary<string, int>();
			foreach (var m in mm)
			{
				if (!sizes.ContainsKey(m.Name) || m.MaxOffs >= sizes[m.Name])
					sizes[m.Name] = m.MaxOffs;
			}

			List<string> keys = new List<string>(sizes.Keys);
			foreach (var key in keys)
			{
				// becase we were looking at offsets, and each bank is 8192 big, we need to add that size
				sizes[key] += 8192;
			}
			return sizes;
		}
	}

	public abstract class CodeDataLog : Dictionary<string, byte[]>
	{
		public CodeDataLog()
			:base()
		{
		}

		/// <summary>
		/// You don't have to use this necessarily, it's just provided for convenience
		/// </summary>
		public bool Active;

		public virtual void Disassemble(Stream s, IMemoryDomains mem) { }

		public abstract string SubType { get; }
		public abstract int SubVer { get; }

		public void LogicalOrFrom(CodeDataLog other)
		{
			if (this.Count != other.Count)
				throw new InvalidDataException("Dictionaries must have the same number of keys!");

			foreach (var kvp in other)
			{
				byte[] fromdata = kvp.Value;
				byte[] todata = this[kvp.Key];

				if (fromdata.Length != todata.Length)
					throw new InvalidDataException("Memory regions must be the same size!");

				for (int i = 0; i < todata.Length; i++)
					todata[i] |= fromdata[i];
			}
		}

		public void ClearData()
		{
			foreach (byte[] data in Values)
				Array.Clear(data, 0, data.Length);
		}

		public void Save(Stream s)
		{
			var w = new BinaryWriter(s);
			w.Write("BIZHAWK-CDL-2");
			w.Write(SubType.PadRight(15));
			w.Write(Count);
			foreach (var kvp in this)
			{
				w.Write(kvp.Key);
				w.Write(kvp.Value.Length);
				w.Write(kvp.Value);
			}
			w.Flush();
		}

		public static CodeDataLog Load(Stream s)
		{
			var br = new BinaryReader(s);
			string id = br.ReadString();
			string FileSubType;
			if (id == "BIZHAWK-CDL-1")
				FileSubType = "PCE";
			else if (id == "BIZHAWK-CDL-2")
				FileSubType = br.ReadString().TrimEnd(' ');
			else
				throw new InvalidDataException("File is not a Bizhawk CDL file!");

			if (FileSubType == "PCE")
				return new CodeDataLog_PCE().Load(br);
			else if(FileSubType == "GB")
				return new CodeDataLog_GB().Load(br);
			else return null;
		}

		private CodeDataLog Load(BinaryReader br)
		{
			int count = br.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				string key = br.ReadString();
				int len = br.ReadInt32();
				byte[] data = br.ReadBytes(len);
				this[key] = data;
			}

			return this;
		}
	}

	public partial class HuC6280
	{
		public struct MemMapping
		{
			public string Name;
			public int Offs;
			public int VOffs; // if non-zero, specifies a larger potential offset
			public int MaxOffs { get { return Math.Max(Offs, VOffs); } }
		}

		public MemMapping[] Mappings; // = new MemMapping[256];

		public CodeDataLog CDL = null;

		public bool CDLLoggingActive = false;

		[Flags]
		public enum CDLUsage : byte
		{
			// was fetched as an opcode first byte
			Code = 0x01,
			// was read or written as data
			Data = 0x02,
			// was read and used as a pointer to data via indirect addressing
			DataPtr = 0x04,
			// was read or written as stack
			Stack = 0x08,
			// was read or written as data via indirect addressing
			IndirectData = 0x10,
			// was read and used as function pointer
			// NB: there is no "IndirectCode"; all code is marked simply as code regardless of how it is reached
			FcnPtr = 0x20,
			// was used as a source or destination (either initial or during the loop) of a block xfer
			BlockXFer = 0x40,
			// was fetched as an operand byte to an opcode
			CodeOperand = 0x80
		}

		void Mark(ushort addr, CDLUsage flag)
		{
			var m = Mappings[MPR[addr >> 13]];
			CDL[m.Name][addr & 0x1fff | m.Offs] |= (byte)flag;
		}

		// mark addr as having been fetched for execute
		void MarkCode(int addr_, int n)
		{
			for (int i = 0; i < n; i++)
			{
				ushort addr = (ushort)(addr_ + i);
				Mark(addr, i == 0 ? CDLUsage.Code : CDLUsage.CodeOperand);
			}
		}

		// mark addr as having been seen as data
		void MarkAddr(int addr_)
		{
			ushort addr = (ushort)addr_;
			Mark(addr, CDLUsage.Data);
		}

		// convert address to zero-page, then mark as data
		void MarkZP(int addr_)
		{
			ushort addr = (ushort)(addr_ & 0xff | 0x2000);
			Mark(addr, CDLUsage.Data);
		}

		// convert address to zero-page, then return the pointer stored there
		ushort GetIndirect(int addr_)
		{
			ushort addr = (ushort)(addr_ & 0xff | 0x2000);
			return ReadWordPageWrap(addr);
		}

		// convert address to zero-page, then mark as pointer (two bytes)
		void MarkZPPtr(int addr_)
		{
			ushort addr = (ushort)(addr_ & 0xff | 0x2000);
			ushort addr2 = (ushort)(addr & 0xff00 | (addr + 1) & 0x00ff);
			Mark(addr, CDLUsage.DataPtr);
			Mark(addr2, CDLUsage.DataPtr);
		}

		// mark address as destination data of an indirect pointer
		void MarkIndirect(int addr_)
		{
			ushort addr = (ushort)addr_;
			Mark(addr, CDLUsage.IndirectData);
		}

		// mark stack space
		void MarkPush(int n)
		{
			for (int i = 0; i < n; i++)
			{
				ushort addr = (ushort)(S - i);
				Mark(addr, CDLUsage.Stack);
			}
		}

		void MarkPop(int n)
		{
			for (int i = 0; i < n; i++)
			{
				ushort addr = (ushort)(S + i + 1);
				Mark(addr, CDLUsage.Stack);
			}
		}

		// mark addr as function pointer (2 bytes)
		void MarkFptr(int addr_)
		{
			ushort addr = (ushort)addr_;
			ushort addr2 = (ushort)(addr & 0xff00 | (addr + 1) & 0x00ff);
			Mark(addr, CDLUsage.FcnPtr);
			Mark(addr2, CDLUsage.FcnPtr);
		}

		// block transfer "from"
		void MarkBTFrom(int addr_)
		{
			ushort addr = (ushort)addr_;
			Mark(addr, CDLUsage.BlockXFer);
		}

		// block transfer "to"
		void MarkBTTo(int addr_)
		{
			ushort addr = (ushort)addr_;
			Mark(addr, CDLUsage.BlockXFer);
		}
	}
}
