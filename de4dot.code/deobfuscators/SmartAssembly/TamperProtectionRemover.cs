/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.SmartAssembly {
	class TamperProtectionRemover {
		ModuleDefMD module;
		List<MethodDef> pinvokeMethods = new List<MethodDef>();

		enum Type {
			V1,
			V2,
		}

		public IList<MethodDef> PinvokeMethods => pinvokeMethods;
		public TamperProtectionRemover(ModuleDefMD module) => this.module = module;

		public bool Remove(Blocks blocks) {
			if (blocks.Method.Name != ".cctor")
				return false;
			return RemoveTamperProtection(blocks);
		}

		bool IsTamperProtected(IEnumerable<Block> allBlocks) {
			foreach (var block in allBlocks) {
				foreach (var instr in block.Instructions) {
					if (instr.OpCode != OpCodes.Ldstr)
						continue;
					var s = instr.Operand as string;
					if (s == "Assembly has been tampered")
						return true;
				}
			}
			return false;
		}

		class BlockInfo {
			public Block Block { get; set; }
			public int Start { get; set; }
			public int End { get; set; }
		}

		class TamperBlocks {
			public Type type;
			public MethodDef pinvokeMethod;
			public BlockInfo first;
			public BlockInfo second;
			public BlockInfo bad;
		}

		TamperBlocks FindTamperBlocks(Blocks blocks, IList<Block> allBlocks) {
			var tamperBlocks = new TamperBlocks();

			if (!FindFirstBlocks(tamperBlocks, allBlocks, blocks.Locals))
				return null;

			var second = tamperBlocks.second;
			var badBlock = second.Block.LastInstr.IsBrfalse() ? second.Block.Targets[0] : second.Block.FallThrough;
			tamperBlocks.bad = FindBadBlock(badBlock);
			if (tamperBlocks.bad == null)
				return null;

			return tamperBlocks;
		}

		bool FindFirstBlocks(TamperBlocks tamperBlocks, IList<Block> allBlocks, IList<Local> locals) {
			foreach (var b in allBlocks) {
				try {
					if (FindFirstBlocks(b, tamperBlocks, allBlocks, locals))
						return true;
				}
				catch (ArgumentOutOfRangeException) {
					continue;
				}
			}

			return false;
		}

		static int FindCallMethod(Block block, int index, bool keepLooking, Func<IMethod, bool> func) {
			var instrs = block.Instructions;
			for (int i = index; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
					continue;

				if (instr.Operand is IMethod calledMethod && func(calledMethod))
					return i;
				if (!keepLooking)
					return -1;
			}
			return -1;
		}

		bool FindFirstBlocks(Block block, TamperBlocks tamperBlocks, IList<Block> allBlocks, IList<Local> locals) {
			if (!block.LastInstr.IsBrfalse())
				return false;

			/*
			 * ldc.i4.0
			 * stloc X
			 * call GetExecutingAssembly()
			 * stloc Y
			 * ldloc Y
			 * callvirt Location
			 * ldc.i4.1
			 * ldloca X
			 * call StrongNameSignatureVerificationEx
			 * pop / brfalse bad_code
			 * ldloc X
			 * brfalse bad_code
			 * ldloc Y
			 * callvirt FullName()
			 * ldstr "......"
			 * callvirt EndsWith(string)
			 * brfalse bad_code / brtrue good_code
			 */

			/*
			0	0000	ldc.i4.0
			1	0001	stloc.1
			2	0002	call	class [mscorlib]System.Reflection.Assembly [mscorlib]System.Reflection.Assembly::GetExecutingAssembly()
			3	0007	stloc.0
			4	0008	ldloc.0
			5	0009	callvirt	instance string [mscorlib]System.Reflection.Assembly::get_CodeBase()
			6	000E	ldstr	"%"
			7	0013	ldstr	"%25"
			8	0018	callvirt	instance string [mscorlib]System.String::Replace(string, string)
			9	001D	ldstr	"#"
			10	0022	ldstr	"%23"
			11	0027	callvirt	instance string [mscorlib]System.String::Replace(string, string)
			12	002C	newobj	instance void [System]System.Uri::.ctor(string)
			13	0031	stloc.2
			14	0032	ldloc.2
			15	0033	callvirt	instance string [System]System.Uri::get_LocalPath()
			16	0038	ldc.i4.1
			17	0039	ldloca.s	V_1 (1)
			18	003B	call	bool LiquidTechnologies.Licensing.ClientLicenseManager2::StrongNameSignatureVerificationEx(string, bool, bool&)
			19	0040	brfalse.s	27 (0057) ldstr "Assembly has been tampered"
			20	0042	ldloc.1
			21	0043	brfalse.s	27 (0057) ldstr "Assembly has been tampered"
			22	0045	ldloc.0
			23	0046	callvirt	instance string [mscorlib]System.Reflection.Assembly::get_FullName()
			24	004B	ldstr	"aabbccddee123456"
			25	0050	callvirt	instance bool [mscorlib]System.String::EndsWith(string)
			26	0055	brtrue.s	30 (0062) ret 
			27	0057	ldstr	"Assembly has been tampered"
			28	005C	newobj	instance void [mscorlib]System.Security.SecurityException::.ctor(string)
			29	0061	throw
			30	0062	ret
			*/

			var instrs = block.Instructions;
			int end = instrs.Count - 1;
			Instr instr;
			IMethod method;
			tamperBlocks.type = Type.V1;

			int index = 0;

			int start = FindCallMethod(block, index, true, (calledMethod) => calledMethod.ToString() == "System.Reflection.Assembly System.Reflection.Assembly::GetExecutingAssembly()");
			if (start < 0)
				return false;
			index = start + 1;
			instr = instrs[--start];
			if (!instr.IsStloc())
				return false;
			var loc0 = Instr.GetLocalVar(locals, instr);
			instr = instrs[--start];
			if (!instr.IsLdcI4())
				return false;

			int index2 = index;
			index = FindCallMethod(block, index, false, (calledMethod) => calledMethod.ToString() == "System.String System.Reflection.Assembly::get_Location()");
			if (index < 0) {
				index = index2;
				index = FindCallMethod(block, index, false, (calledMethod) => calledMethod.ToString() == "System.String System.Reflection.Assembly::get_CodeBase()");
				if (index < 0)
					return false;
			}
			index++;
			index2 = index;

			index = FindCallMethod(block, index, false, (calledMethod) => {
				tamperBlocks.pinvokeMethod = DotNetUtils.GetMethod(module, calledMethod);
				return DotNetUtils.IsPinvokeMethod(tamperBlocks.pinvokeMethod, "mscorwks", "StrongNameSignatureVerificationEx");
			});
			if (index < 0) {
				index = index2;

				index = FindCallMethod(block, index, false, (calledMethod) => calledMethod.ToString() == "System.String System.String::Replace(System.String,System.String)");
				if (index < 0) return false;
				index++;
				index2 = index;

				index = FindCallMethod(block, index, false, (calledMethod) => calledMethod.ToString() == "System.String System.String::Replace(System.String,System.String)");
				if (index < 0) return false;
				index++;
				index2 = index;

				index = FindCallMethod(block, index, false, (calledMethod) => calledMethod.ToString() == "System.String System.Uri::get_LocalPath()");
				if (index < 0) return false;
				index2 = index + 1;
				instr = instrs[--index];
				if (!instr.IsLdloc()) return false;
				instr = instrs[--index];
				if (!instr.IsStloc()) return false;
				index = index2;

				index = FindCallMethod(block, index, false, (calledMethod) => {
					tamperBlocks.pinvokeMethod = DotNetUtils.GetMethod(module, calledMethod);
					return DotNetUtils.IsPinvokeMethod(tamperBlocks.pinvokeMethod, "mscorwks", "StrongNameSignatureVerificationEx");
				});
				if (index < 0) return false;
			}
			index++;

			if (!instrs[index].IsBrfalse()) {
				if (instrs[index].OpCode.Code != Code.Pop)
					return false;
				instr = instrs[index + 1];
				if (!instr.IsLdloc() || Instr.GetLocalVar(locals, instr) != loc0)
					return false;
				if (!instrs[index + 2].IsBrfalse())
					return false;

				tamperBlocks.type = Type.V1;
				tamperBlocks.first = new BlockInfo {
					Block = block,
					Start = start,
					End = end,
				};
			}
			else {
				tamperBlocks.type = Type.V2;
				tamperBlocks.first = new BlockInfo {
					Block = block,
					Start = start,
					End = end,
				};

				block = block.FallThrough;
				if (block == null)
					return false;
				instrs = block.Instructions;
				index = 0;
				instr = instrs[index];
				if (!instr.IsLdloc() || Instr.GetLocalVar(locals, instr) != loc0)
					return false;
				if (!instrs[index + 1].IsBrfalse())
					return false;
			}

			block = block.FallThrough;
			instrs = block.Instructions;
			start = end = 0;

			instr = instrs[end++];
			if (!instr.IsLdloc())
				return false;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Callvirt)
				return false;
			method = instr.Operand as IMethod;
			if (method == null || method.ToString() != "System.String System.Reflection.Assembly::get_FullName()")
				return false;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Ldstr)
				return false;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Callvirt)
				return false;
			method = instr.Operand as IMethod;
			if (method == null || method.ToString() != "System.Boolean System.String::EndsWith(System.String)")
				return false;

			instr = instrs[end++];
			if (!instr.IsBrfalse() && !instr.IsBrtrue())
				return false;

			end--;
			tamperBlocks.second = new BlockInfo {
				Block = block,
				Start = start,
				End = end,
			};

			return true;
		}

		BlockInfo FindBadBlock(Block last) {
			/*
			 * ldstr "........."
			 * newobj	System.Security.SecurityException(string)
			 * throw
			 */

			var instrs = last.Instructions;
			if (instrs.Count != 3)
				return null;

			Instr instr;
			int start = 0;
			int end = 0;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Ldstr)
				return null;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Newobj)
				return null;
			var method = instr.Operand as IMethod;
			if (method == null || method.ToString() != "System.Void System.Security.SecurityException::.ctor(System.String)")
				return null;

			instr = instrs[end++];
			if (instr.OpCode != OpCodes.Throw)
				return null;

			end--;
			return new BlockInfo {
				Block = last,
				Start = start,
				End = end,
			};
		}

		bool RemoveTamperProtection(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			var tamperBlocks = FindTamperBlocks(blocks, allBlocks);

			if (tamperBlocks == null) {
				if (IsTamperProtected(allBlocks))
					Logger.w("Could not remove tamper protection code: {0} ({1:X8})", Utils.RemoveNewlines(blocks.Method), blocks.Method.MDToken.ToUInt32());
				return false;
			}

			switch (tamperBlocks.type) {
			case Type.V1:
				RemoveTamperV1(tamperBlocks);
				break;
			case Type.V2:
				RemoveTamperV2(tamperBlocks);
				break;
			default:
				throw new ApplicationException("Unknown type");
			}
			pinvokeMethods.Add(tamperBlocks.pinvokeMethod);

			return true;
		}

		void RemoveTamperV1(TamperBlocks tamperBlocks) {
			var first = tamperBlocks.first;
			var second = tamperBlocks.second;
			var bad = tamperBlocks.bad;
			var goodBlock = second.Block.LastInstr.IsBrtrue() ? second.Block.Targets[0] : second.Block.FallThrough;

			if (first.Block.Targets.Count != 1 || first.Block.Targets[0] != bad.Block)
				throw new ApplicationException("Invalid state");

			first.Block.Remove(first.Start, first.End - first.Start + 1);
			first.Block.ReplaceLastInstrsWithBranch(0, goodBlock);
			RemoveDeadBlock(second.Block);
			RemoveDeadBlock(bad.Block);
		}

		void RemoveTamperV2(TamperBlocks tamperBlocks) {
			var first = tamperBlocks.first;
			var second = tamperBlocks.second.Block;
			var bad = tamperBlocks.bad.Block;
			var firstFallthrough = first.Block.FallThrough;
			var goodBlock = second.LastInstr.IsBrtrue() ? second.Targets[0] : second.FallThrough;

			if (first.Block.Targets.Count != 1 || first.Block.Targets[0] != bad)
				throw new ApplicationException("Invalid state");

			first.Block.Remove(first.Start, first.End - first.Start + 1);
			first.Block.ReplaceLastInstrsWithBranch(0, goodBlock);
			RemoveDeadBlock(firstFallthrough);
			RemoveDeadBlock(second);
			RemoveDeadBlock(bad);
		}

		void RemoveDeadBlock(Block block) {
			var parent = block.Parent;
			if (parent != null) // null if already dead
				parent.RemoveDeadBlock(block);
		}
	}
}
