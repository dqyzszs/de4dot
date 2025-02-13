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
using System.Text;
using de4dot.blocks;
using de4dot.code.renamer.asmmodules;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.renamer {
	public class ResourceRenamer {
		Module module;
		Dictionary<string, Resource> nameToResource;

		public ResourceRenamer(Module module) => this.module = module;

		public void Rename(List<TypeInfo> renamedTypes) {
			// Rename the longest names first. Otherwise eg. b.g.resources could be renamed
			// Class0.g.resources instead of Class1.resources when b.g was renamed Class1.
			renamedTypes.Sort((a, b) => {
				var aesc = EscapeTypeName(a.oldFullName);
				var besc = EscapeTypeName(b.oldFullName);
				if (besc.Length != aesc.Length)
					return besc.Length.CompareTo(aesc.Length);
				return besc.CompareTo(aesc);
			});

			nameToResource = new Dictionary<string, Resource>(module.ModuleDefMD.Resources.Count * 3, StringComparer.Ordinal);
			foreach (var resource in module.ModuleDefMD.Resources) {
				var name = resource.Name.String;
				nameToResource[name] = resource;
				if (name.EndsWith(".g.resources"))
					nameToResource[name.Substring(0, name.Length - 12)] = resource;
				int index = name.LastIndexOf('.');
				if (index > 0)
					nameToResource[name.Substring(0, index)] = resource;
			}

			RenameResourceNamesInCode(renamedTypes);
			RenameResources(renamedTypes);
		}

		void RenameResourceNamesInCode(List<TypeInfo> renamedTypes) {
			var oldNameToTypeInfo = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
			foreach (var info in renamedTypes)
				oldNameToTypeInfo[EscapeTypeName(info.oldFullName)] = info;

			foreach (var method in module.GetAllMethods()) {
				if (!method.HasBody)
					continue;
				var instrs = method.Body.Instructions;
				for (int i = 0; i < instrs.Count; i++) {
					var instr = instrs[i];
					if (instr.OpCode != OpCodes.Ldstr)
						continue;
					var codeString = (string)instr.Operand;
					if (string.IsNullOrEmpty(codeString))
						continue;

					if (!nameToResource.TryGetValue(codeString, out var resource))
						continue;

					if (!oldNameToTypeInfo.TryGetValue(codeString, out var typeInfo))
						continue;
					var newName = EscapeTypeName(typeInfo.type.TypeDef.FullName);

					bool renameCodeString = module.ObfuscatedFile.RenameResourcesInCode ||
											IsCallingResourceManagerCtor(instrs, i, typeInfo);
					if (!renameCodeString)
						Logger.v("Possible resource name in code: '{0}' => '{1}' in method {2}", Utils.RemoveNewlines(codeString), newName, Utils.RemoveNewlines(method));
					else {
						instr.Operand = newName;
						Logger.v("Renamed resource string in code: '{0}' => '{1}' ({2})", Utils.RemoveNewlines(codeString), newName, Utils.RemoveNewlines(method));
					}
				}
			}
		}

		static bool IsCallingResourceManagerCtor(IList<Instruction> instrs, int ldstrIndex, TypeInfo typeInfo) {
			try {
				int index = ldstrIndex + 1;

				var ldtoken = instrs[index++];
				if (ldtoken.OpCode.Code != Code.Ldtoken)
					return false;
				if (!new SigComparer().Equals(typeInfo.type.TypeDef, ldtoken.Operand as ITypeDefOrRef))
					return false;

				if (!CheckCalledMethod(instrs[index++], "System.Type", "(System.RuntimeTypeHandle)"))
					return false;
				if (!CheckCalledMethod(instrs[index++], "System.Reflection.Assembly", "()"))
					return false;

				var newobj = instrs[index++];
				if (newobj.OpCode.Code != Code.Newobj)
					return false;
				if (newobj.Operand.ToString() != "System.Void System.Resources.ResourceManager::.ctor(System.String,System.Reflection.Assembly)")
					return false;

				return true;
			}
			catch (ArgumentOutOfRangeException) {
				return false;
			}
			catch (IndexOutOfRangeException) {
				return false;
			}
		}

		static bool CheckCalledMethod(Instruction instr, string returnType, string parameters) {
			if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
				return false;
			return DotNetUtils.IsMethod(instr.Operand as IMethod, returnType, parameters);
		}

		class RenameInfo {
			public Resource resource;
			public TypeInfo typeInfo;
			public string newResourceName;
			public RenameInfo(Resource resource, TypeInfo typeInfo, string newResourceName) {
				this.resource = resource;
				this.typeInfo = typeInfo;
				this.newResourceName = newResourceName;
			}
			public override string ToString() => $"{resource.Name} => {newResourceName}";
		}

		void RenameResources(List<TypeInfo> renamedTypes) {
			var newNames = new Dictionary<Resource, RenameInfo>();
			foreach (var info in renamedTypes) {
				var oldFullName = EscapeTypeName(info.oldFullName);
				if (!nameToResource.TryGetValue(oldFullName, out var resource))
					continue;
				if (newNames.ContainsKey(resource))
					continue;
				var newTypeName = EscapeTypeName(info.type.TypeDef.FullName);
				var newName = newTypeName + resource.Name.String.Substring(oldFullName.Length);
				newNames[resource] = new RenameInfo(resource, info, newName);

				Logger.v("Renamed resource in resources: {0} => {1}", Utils.RemoveNewlines(resource.Name), newName);
				resource.Name = newName;
			}
		}

		static bool IsReservedTypeNameChar(char c) {
			switch (c) {
			case ',':
			case '[':
			case ']':
			case '&':
			case '*':
			case '+':
			case '\\':
				return true;
			default:
				return false;
			}
		}

		static bool HasReservedTypeNameChar(string s) {
			foreach (var c in s) {
				if (IsReservedTypeNameChar(c))
					return true;
			}
			return false;
		}

		static string EscapeTypeName(string name) {
			if (!HasReservedTypeNameChar(name))
				return name;
			var sb = new StringBuilder();
			foreach (var c in name) {
				if (IsReservedTypeNameChar(c))
					sb.Append('\\');
				sb.Append(c);
			}
			return sb.ToString();
		}
	}
}
