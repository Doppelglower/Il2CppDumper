using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper
{
    public class StructGenerator
    {
        private readonly Il2CppExecutor executor;
        private readonly Metadata metadata;
        private readonly Il2Cpp il2Cpp;
        private readonly Dictionary<Il2CppTypeDefinition, string> typeDefImageNames = new();
        private readonly HashSet<string> structNameHashSet = new(StringComparer.Ordinal);
        private readonly List<StructInfo> structInfoList = new();
        private readonly Dictionary<string, StructInfo> structInfoWithStructName = new();
        private readonly HashSet<StructInfo> structCache = new();
        private readonly Dictionary<Il2CppTypeDefinition, string> structNameDic = new();
        private readonly Dictionary<ulong, string> genericClassStructNameDic = new();
        private readonly Dictionary<string, Il2CppType> nameGenericClassDic = new();
        private readonly List<ulong> genericClassList = new();
        private readonly StringBuilder arrayClassHeader = new();
        private readonly StringBuilder methodInfoHeader = new();
        private readonly Dictionary<Il2CppTypeDefinition, int> typeDefIndices = new();
        private readonly List<EnumInfo> enumInfoList = new();
        private readonly Dictionary<string, uint> enumSizeByName = new();
        private readonly HashSet<string> enumMemberNames = new(StringComparer.Ordinal);
        private static readonly HashSet<ulong> methodInfoCache = new();
        private const long MaxGenericArgumentCount = 1024;
        private static readonly HashSet<string> keyword = new(StringComparer.Ordinal)
        { "klass", "monitor", "register", "_cs", "auto", "friend", "template", "flat", "default", "_ds", "interrupt",
            "unsigned", "signed", "asm", "if", "case", "break", "continue", "do", "new", "_", "short", "union", "class", "namespace", "enum"};
        private static readonly HashSet<string> specialKeywords = new(StringComparer.Ordinal)
        { "inline", "near", "far" };

        public StructGenerator(Il2CppExecutor il2CppExecutor)
        {
            executor = il2CppExecutor;
            metadata = il2CppExecutor.metadata;
            il2Cpp = il2CppExecutor.il2Cpp;
        }

        public void WriteScript(string outputDir)
        {
            var json = new ScriptJson();
            // 生成唯一名称
            for (var imageIndex = 0; imageIndex < metadata.imageDefs.Length; imageIndex++)
            {
                var imageDef = metadata.imageDefs[imageIndex];
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int typeIndex = imageDef.typeStart; typeIndex < typeEnd; typeIndex++)
                {
                    var typeDef = metadata.typeDefs[typeIndex];
                    typeDefImageNames.Add(typeDef, imageName);
                    typeDefIndices[typeDef] = typeIndex;
                    CreateStructNameDic(typeDef);
                }
            }
            // 生成后面处理泛型实例要用到的字典
            foreach (var il2CppType in il2Cpp.types.Where(x => x.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST))
            {
                var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                if (typeDef == null)
                {
                    continue;
                }
                var typeBaseName = structNameDic[typeDef];
                var typeToReplaceName = FixName(executor.GetTypeDefName(typeDef, true, true));
                var typeReplaceName = FixName(executor.GetTypeName(il2CppType, true, false));
                var typeStructName = typeBaseName.Replace(typeToReplaceName, typeReplaceName);
                nameGenericClassDic[typeStructName] = il2CppType;
                genericClassStructNameDic[il2CppType.data.generic_class] = typeStructName;
            }
            // 处理函数
            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int typeIndex = imageDef.typeStart; typeIndex < typeEnd; typeIndex++)
                {
                    var typeDef = metadata.typeDefs[typeIndex];
                    if (typeDef.IsEnum)
                    {
                        AddEnum(typeDef);
                    }
                    else
                    {
                        AddStruct(typeDef, typeIndex);
                    }
                    var typeName = executor.GetTypeDefName(typeDef, true, true);
                    for (var methodIndexInType = 0; methodIndexInType < typeDef.method_count; methodIndexInType++)
                    {
                        var i = metadata.GetMethodIndexFromTypeDefinition(typeIndex, methodIndexInType);
                        var methodDef = metadata.methodDefs[i];
                        var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                        var methodPointer = il2Cpp.GetMethodPointer(imageName, methodDef);
                        if (methodPointer > 0)
                        {
                            var methodTypeSignature = new List<Il2CppTypeEnum>();
                            var scriptMethod = new ScriptMethod();
                            json.ScriptMethod.Add(scriptMethod);
                            scriptMethod.Address = il2Cpp.GetRVA(methodPointer);
                            var methodFullName = typeName + "$$" + methodName;
                            scriptMethod.Name = methodFullName;

                            var methodReturnType = il2Cpp.types[methodDef.returnType];
                            var returnType = ParseType(methodReturnType);
                            if (methodReturnType.byref == 1)
                            {
                                returnType += "*";
                            }
                            methodTypeSignature.Add(methodReturnType.byref == 1 ? Il2CppTypeEnum.IL2CPP_TYPE_PTR : methodReturnType.type);
                            var signature = $"{returnType} {FixName(methodFullName)} (";
                            var parameterStrs = new List<string>();
                            if ((methodDef.flags & METHOD_ATTRIBUTE_STATIC) == 0)
                            {
                                var thisType = ParseType(il2Cpp.types[typeDef.byvalTypeIndex]);
                                methodTypeSignature.Add(il2Cpp.types[typeDef.byvalTypeIndex].type);
                                parameterStrs.Add($"{thisType} __this");
                            }
                            else if (il2Cpp.Version <= 24)
                            {
                                methodTypeSignature.Add(Il2CppTypeEnum.IL2CPP_TYPE_PTR);
                                parameterStrs.Add($"Il2CppObject* __this");
                            }
                            for (var j = 0; j < methodDef.parameterCount; j++)
                            {
                                var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                                var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                                var parameterType = il2Cpp.types[parameterDef.typeIndex];
                                var parameterCType = ParseType(parameterType);
                                if (parameterType.byref == 1)
                                {
                                    parameterCType += "*";
                                }
                                methodTypeSignature.Add(parameterType.byref == 1 ? Il2CppTypeEnum.IL2CPP_TYPE_PTR : parameterType.type);
                                parameterStrs.Add($"{parameterCType} {FixName(parameterName)}");
                            }
                            methodTypeSignature.Add(Il2CppTypeEnum.IL2CPP_TYPE_PTR);
                            parameterStrs.Add("const MethodInfo* method");
                            signature += string.Join(", ", parameterStrs);
                            signature += ");";
                            scriptMethod.Signature = signature;
                            scriptMethod.TypeSignature = GetMethodTypeSignature(methodTypeSignature);
                        }
                        //泛型实例函数
                        if (il2Cpp.methodDefinitionMethodSpecs.TryGetValue(i, out var methodSpecs))
                        {
                            foreach (var methodSpec in methodSpecs)
                            {
                                var genericMethodPointer = il2Cpp.methodSpecGenericMethodPointers[methodSpec];
                                if (genericMethodPointer > 0)
                                {
                                    var methodTypeSignature = new List<Il2CppTypeEnum>();
                                    var scriptMethod = new ScriptMethod();
                                    json.ScriptMethod.Add(scriptMethod);
                                    scriptMethod.Address = il2Cpp.GetRVA(genericMethodPointer);
                                    var methodInfoName = $"MethodInfo_{scriptMethod.Address:X}";
                                    var structTypeName = structNameDic[typeDef];
                                    var rgctxs = GenerateRGCTX(imageName, methodDef);
                                    if (methodInfoCache.Add(genericMethodPointer))
                                    {
                                        GenerateMethodInfo(methodInfoName, structTypeName, rgctxs);
                                    }
                                    (var methodSpecTypeName, var methodSpecMethodName) = executor.GetMethodSpecName(methodSpec, true);
                                    var methodFullName = methodSpecTypeName + "$$" + methodSpecMethodName;
                                    scriptMethod.Name = methodFullName;

                                    var genericContext = executor.GetMethodSpecGenericContext(methodSpec);
                                    var methodReturnType = il2Cpp.types[methodDef.returnType];
                                    var returnType = ParseType(methodReturnType, genericContext);
                                    if (methodReturnType.byref == 1)
                                    {
                                        returnType += "*";
                                    }
                                    methodTypeSignature.Add(methodReturnType.byref == 1 ? Il2CppTypeEnum.IL2CPP_TYPE_PTR : methodReturnType.type);
                                    var signature = $"{returnType} {FixName(methodFullName)} (";
                                    var parameterStrs = new List<string>();
                                    if ((methodDef.flags & METHOD_ATTRIBUTE_STATIC) == 0)
                                    {
                                        string thisType;
                                        if (methodSpec.classIndexIndex != -1)
                                        {
                                            var typeBaseName = structNameDic[typeDef];
                                            var typeToReplaceName = FixName(typeName);
                                            var typeReplaceName = FixName(methodSpecTypeName);
                                            var typeStructName = typeBaseName.Replace(typeToReplaceName, typeReplaceName);
                                            if (nameGenericClassDic.TryGetValue(typeStructName, out var il2CppType))
                                            {
                                                thisType = ParseType(il2CppType);
                                                methodTypeSignature.Add(il2CppType.type);
                                            }
                                            else
                                            {
                                                //没有单独的泛型实例类
                                                thisType = ParseType(il2Cpp.types[typeDef.byvalTypeIndex]);
                                                methodTypeSignature.Add(il2Cpp.types[typeDef.byvalTypeIndex].type);
                                            }
                                        }
                                        else
                                        {
                                            thisType = ParseType(il2Cpp.types[typeDef.byvalTypeIndex]);
                                            methodTypeSignature.Add(il2Cpp.types[typeDef.byvalTypeIndex].type);
                                        }
                                        parameterStrs.Add($"{thisType} __this");
                                    }
                                    else if (il2Cpp.Version <= 24)
                                    {
                                        methodTypeSignature.Add(Il2CppTypeEnum.IL2CPP_TYPE_PTR);
                                        parameterStrs.Add($"Il2CppObject* __this");
                                    }
                                    for (var j = 0; j < methodDef.parameterCount; j++)
                                    {
                                        var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                                        var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                                        var parameterType = il2Cpp.types[parameterDef.typeIndex];
                                        var parameterCType = ParseType(parameterType, genericContext);
                                        if (parameterType.byref == 1)
                                        {
                                            parameterCType += "*";
                                        }
                                        methodTypeSignature.Add(parameterType.byref == 1 ? Il2CppTypeEnum.IL2CPP_TYPE_PTR : parameterType.type);
                                        parameterStrs.Add($"{parameterCType} {FixName(parameterName)}");
                                    }
                                    methodTypeSignature.Add(Il2CppTypeEnum.IL2CPP_TYPE_PTR);
                                    parameterStrs.Add($"const {methodInfoName}* method");
                                    signature += string.Join(", ", parameterStrs);
                                    signature += ");";
                                    scriptMethod.Signature = signature;
                                    scriptMethod.TypeSignature = GetMethodTypeSignature(methodTypeSignature);
                                }
                            }
                        }
                    }
                }
            }
            //处理函数范围
            List<ulong> orderedPointers;
            if (il2Cpp.Version >= 24.2)
            {
                orderedPointers = new List<ulong>();
                foreach (var pair in il2Cpp.codeGenModuleMethodPointers)
                {
                    orderedPointers.AddRange(pair.Value);
                }
            }
            else
            {
                orderedPointers = il2Cpp.methodPointers.ToList();
            }
            orderedPointers.AddRange(il2Cpp.genericMethodPointers);
            orderedPointers.AddRange(il2Cpp.invokerPointers);
            if (il2Cpp.Version < 29)
            {
                orderedPointers.AddRange(executor.customAttributeGenerators);
            }
            if (il2Cpp.Version >= 22)
            {
                if (il2Cpp.reversePInvokeWrappers != null)
                    orderedPointers.AddRange(il2Cpp.reversePInvokeWrappers);
                if (il2Cpp.unresolvedVirtualCallPointers != null)
                    orderedPointers.AddRange(il2Cpp.unresolvedVirtualCallPointers);
            }
            //TODO interopData内也包含函数
            orderedPointers = orderedPointers.Distinct().OrderBy(x => x).ToList();
            orderedPointers.Remove(0);
            json.Addresses = new ulong[orderedPointers.Count];
            for (int i = 0; i < orderedPointers.Count; i++)
            {
                json.Addresses[i] = il2Cpp.GetRVA(orderedPointers[i]);
            }
            // 处理MetadataUsage
            if (il2Cpp.Version >= 27)
            {
                var sectionHelper = executor.GetSectionHelper();
                foreach (var sec in sectionHelper.Data)
                {
                    il2Cpp.Position = sec.offset;
                    var end = Math.Min(sec.offsetEnd, il2Cpp.Length) - il2Cpp.PointerSize;
                    while (il2Cpp.Position < end)
                    {
                        var addr = il2Cpp.Position;
                        var metadataValue = il2Cpp.ReadUIntPtr();
                        var position = il2Cpp.Position;
                        if (metadataValue < uint.MaxValue)
                        {
                            var encodedToken = (uint)metadataValue;
                            var usage = metadata.GetEncodedIndexTypeForVersion(encodedToken);
                            if (usage > 0 && usage <= 6)
                            {
                                var decodedIndex = metadata.GetDecodedMethodIndex(encodedToken);
                                if (metadataValue == ((usage << 29) | (decodedIndex << 1)) + 1)
                                {
                                    var va = il2Cpp.MapRTVA(addr);
                                    if (va > 0)
                                    {
                                        switch ((Il2CppMetadataUsage)usage)
                                        {
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageInvalid:
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageTypeInfo:
                                                if (decodedIndex < il2Cpp.types.Length)
                                                {
                                                    AddMetadataUsageTypeInfo(json, decodedIndex, va);
                                                }
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageIl2CppType:
                                                if (decodedIndex < il2Cpp.types.Length)
                                                {
                                                    AddMetadataUsageIl2CppType(json, decodedIndex, va);
                                                }
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageMethodDef:
                                                if (decodedIndex < metadata.methodDefs.Length)
                                                {
                                                    AddMetadataUsageMethodDef(json, decodedIndex, va);
                                                }
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageFieldInfo:
                                                if (decodedIndex < metadata.fieldRefs.Length)
                                                {
                                                    AddMetadataUsageFieldInfo(json, decodedIndex, va);
                                                }
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageStringLiteral:
                                                if (decodedIndex < metadata.stringLiterals.Length)
                                                {
                                                    AddMetadataUsageStringLiteral(json, decodedIndex, va);
                                                }
                                                break;
                                            case Il2CppMetadataUsage.kIl2CppMetadataUsageMethodRef:
                                                if (decodedIndex < il2Cpp.methodSpecs.Length)
                                                {
                                                    AddMetadataUsageMethodRef(json, decodedIndex, va);
                                                }
                                                break;
                                        }
                                        if (il2Cpp.Position != position)
                                        {
                                            il2Cpp.Position = position;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (il2Cpp.Version > 16 && il2Cpp.Version < 27)
            {
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageTypeInfo])
                {
                    AddMetadataUsageTypeInfo(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageIl2CppType])
                {
                    AddMetadataUsageIl2CppType(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageMethodDef])
                {
                    AddMetadataUsageMethodDef(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageFieldInfo])
                {
                    AddMetadataUsageFieldInfo(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageStringLiteral])
                {
                    AddMetadataUsageStringLiteral(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
                foreach (var i in metadata.metadataUsageDic[Il2CppMetadataUsage.kIl2CppMetadataUsageMethodRef])
                {
                    AddMetadataUsageMethodRef(json, i.Value, il2Cpp.metadataUsages[i.Key]);
                }
            }
            //输出单独的StringLiteral
            var stringLiterals = json.ScriptString.Select(x => new
            {
                value = x.Value,
                address = $"0x{x.Address:X}"
            }).ToArray();
            var jsonOptions = new JsonSerializerOptions() { WriteIndented = true, IncludeFields = true };
            File.WriteAllText(outputDir + "stringliteral.json", JsonSerializer.Serialize(stringLiterals, jsonOptions), new UTF8Encoding(false));
            //写入文件
            File.WriteAllText(outputDir + "script.json", JsonSerializer.Serialize(json, jsonOptions));
            //il2cpp.h
            for (int i = 0; i < genericClassList.Count; i++)
            {
                var pointer = genericClassList[i];
                AddGenericClassStruct(pointer);
            }
            var headerStruct = new StringBuilder();
            foreach (var info in structInfoList)
            {
                structInfoWithStructName.Add(info.TypeName + "_o", info);
            }
            ComputeAllFieldsSizes();
            var enumHeader = BuildEnumHeader();
            foreach (var info in structInfoList)
            {
                headerStruct.Append(RecursionStructInfo(info));
            }
            var sb = new StringBuilder();
            sb.Append(HeaderConstants.GenericHeader);
            if (il2Cpp.Version >= 35)
            {
                sb.Append(HeaderConstants.HeaderV35Plus);
            }
            else switch (il2Cpp.Version)
            {
                case 22:
                    sb.Append(HeaderConstants.HeaderV22);
                    break;
                case 23:
                case 24:
                    sb.Append(HeaderConstants.HeaderV240);
                    break;
                case 24.1:
                    sb.Append(HeaderConstants.HeaderV241);
                    break;
                case 24.2:
                case 24.3:
                case 24.4:
                case 24.5:
                    sb.Append(HeaderConstants.HeaderV242);
                    break;
                case 27:
                case 27.1:
                case 27.2:
                    sb.Append(HeaderConstants.HeaderV27);
                    break;
                case 29:
                case 29.1:
                case 31:
                    sb.Append(HeaderConstants.HeaderV29);
                    break;
                default:
                    Console.WriteLine($"WARNING: This il2cpp version [{il2Cpp.Version}] does not support generating .h files");
                    return;
            }
            sb.Append(enumHeader);
            sb.Append("#pragma pack(push, 1)\n");
            sb.Append(headerStruct);
            sb.Append(arrayClassHeader);
            sb.Append("#pragma pack(pop)\n");
            sb.Append(methodInfoHeader);
            File.WriteAllText(outputDir + "il2cpp.h", sb.ToString());
        }

        private void AddMetadataUsageTypeInfo(ScriptJson json, uint index, ulong address)
        {
            var type = il2Cpp.types[index];
            var typeName = executor.GetTypeName(type, true, false);
            var scriptMetadata = new ScriptMetadata();
            json.ScriptMetadata.Add(scriptMetadata);
            scriptMetadata.Address = il2Cpp.GetRVA(address);
            scriptMetadata.Name = typeName + "_TypeInfo";
            var signature = GetIl2CppStructName(type);
            if (signature.EndsWith("_array"))
            {
                scriptMetadata.Signature = "Il2CppClass*";
            }
            else
            {
                scriptMetadata.Signature = FixName(signature) + "_c*";
            }
        }

        private void AddMetadataUsageIl2CppType(ScriptJson json, uint index, ulong address)
        {
            var type = il2Cpp.types[index];
            var typeName = executor.GetTypeName(type, true, false);
            var scriptMetadata = new ScriptMetadata();
            json.ScriptMetadata.Add(scriptMetadata);
            scriptMetadata.Address = il2Cpp.GetRVA(address);
            scriptMetadata.Name = typeName + "_var";
            scriptMetadata.Signature = "Il2CppType*";
        }

        private void AddMetadataUsageMethodDef(ScriptJson json, uint index, ulong address)
        {
            var methodDef = metadata.methodDefs[index];
            var typeDef = metadata.typeDefs[methodDef.declaringType];
            var typeName = executor.GetTypeDefName(typeDef, true, true);
            var methodName = typeName + "." + metadata.GetStringFromIndex(methodDef.nameIndex) + "()";
            var scriptMetadataMethod = new ScriptMetadataMethod();
            json.ScriptMetadataMethod.Add(scriptMetadataMethod);
            scriptMetadataMethod.Address = il2Cpp.GetRVA(address);
            scriptMetadataMethod.Name = "Method$" + methodName;
            var imageName = typeDefImageNames[typeDef];
            var methodPointer = il2Cpp.GetMethodPointer(imageName, methodDef);
            if (methodPointer > 0)
            {
                scriptMetadataMethod.MethodAddress = il2Cpp.GetRVA(methodPointer);
            }
        }

        private void AddMetadataUsageFieldInfo(ScriptJson json, uint index, ulong address)
        {
            var fieldRef = metadata.fieldRefs[index];
            var type = il2Cpp.types[fieldRef.typeIndex];
            var typeDef = GetTypeDefinition(type);
            var fieldDef = metadata.fieldDefs[typeDef.fieldStart + fieldRef.fieldIndex];
            var fieldName = executor.GetTypeName(type, true, false) + "." + metadata.GetStringFromIndex(fieldDef.nameIndex);
            var scriptMetadata = new ScriptMetadata();
            json.ScriptMetadata.Add(scriptMetadata);
            scriptMetadata.Address = il2Cpp.GetRVA(address);
            scriptMetadata.Name = "Field$" + fieldName;
        }

        private void AddMetadataUsageStringLiteral(ScriptJson json, uint index, ulong address)
        {
            var scriptString = new ScriptString();
            json.ScriptString.Add(scriptString);
            scriptString.Address = il2Cpp.GetRVA(address);
            scriptString.Value = metadata.GetStringLiteralFromIndex(index);
        }

        private void AddMetadataUsageMethodRef(ScriptJson json, uint index, ulong address)
        {
            var methodSpec = il2Cpp.methodSpecs[index];
            var scriptMetadataMethod = new ScriptMetadataMethod();
            json.ScriptMetadataMethod.Add(scriptMetadataMethod);
            scriptMetadataMethod.Address = il2Cpp.GetRVA(address);
            (var methodSpecTypeName, var methodSpecMethodName) = executor.GetMethodSpecName(methodSpec, true);
            scriptMetadataMethod.Name = "Method$" + methodSpecTypeName + "." + methodSpecMethodName + "()";
            if (il2Cpp.methodSpecGenericMethodPointers.ContainsKey(methodSpec))
            {
                var genericMethodPointer = il2Cpp.methodSpecGenericMethodPointers[methodSpec];
                if (genericMethodPointer > 0)
                {
                    scriptMetadataMethod.MethodAddress = il2Cpp.GetRVA(genericMethodPointer);
                }
            }
        }

        private static string FixName(string str)
        {
            if (keyword.Contains(str))
            {
                str = "_" + str;
            }
            else if (specialKeywords.Contains(str))
            {
                str = "_" + str + "_";
            }

            if (Regex.IsMatch(str, "^[0-9]"))
            {
                return "_" + str;
            }
            else
            {
                return Regex.Replace(str, "[^a-zA-Z0-9_]", "_");
            }
        }

        private string ParseType(Il2CppType il2CppType, Il2CppGenericContext context = null)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                    return "void";
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                    return "bool";
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return "uint16_t"; //Il2CppChar
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                    return "int8_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return "uint8_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                    return "int16_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return "uint16_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                    return "int32_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                    return "uint32_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                    return "int64_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                    return "uint64_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return "float";
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return "double";
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                    return "System_String_o*";
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return ParseType(oriType) + "*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        if (typeDef.IsEnum)
                        {
                            // IDA parse_decls resolves typedef names, not "enum Tag" when the stub is typedef int32_t Tag.
                            return structNameDic[typeDef];
                        }
                        return structNameDic[typeDef] + "_o";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        return structNameDic[typeDef] + "_o*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                        {
                            return ParseType(type);
                        }
                        return "Il2CppObject*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        var elementType = il2Cpp.GetIl2CppType(arrayType.etype);
                        var elementStructName = GetIl2CppStructName(elementType, context);
                        var typeStructName = elementStructName + "_array";
                        if (structNameHashSet.Add(typeStructName))
                        {
                            ParseArrayClassStruct(elementType, context);
                        }
                        return typeStructName + "*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        var typeStructName = genericClassStructNameDic[il2CppType.data.generic_class];
                        if (structNameHashSet.Add(typeStructName))
                        {
                            genericClassList.Add(il2CppType.data.generic_class);
                        }
                        if (typeDef.IsValueType)
                        {
                            if (typeDef.IsEnum)
                            {
                                return typeStructName;
                            }
                            return typeStructName + "_o";
                        }
                        return typeStructName + "_o*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                    return "Il2CppObject*";
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                    return "intptr_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return "uintptr_t";
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    return "Il2CppObject*";
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        var elementType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        var elementStructName = GetIl2CppStructName(elementType, context);
                        var typeStructName = elementStructName + "_array";
                        if (structNameHashSet.Add(typeStructName))
                        {
                            ParseArrayClassStruct(elementType, context);
                        }
                        return typeStructName + "*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, true, out var type))
                        {
                            return ParseType(type);
                        }
                        return "Il2CppObject*";
                    }
                default:
                    throw new NotSupportedException();
            }
        }
        public static string GetMethodTypeSignature(List<Il2CppTypeEnum> types)
        {
            string signature = string.Empty;
            foreach (Il2CppTypeEnum type in types)
            {
                signature += type switch
                {
                    Il2CppTypeEnum.IL2CPP_TYPE_VOID => "v",
                    Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => "i",
                    Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 => "j",
                    Il2CppTypeEnum.IL2CPP_TYPE_R4 => "f",
                    Il2CppTypeEnum.IL2CPP_TYPE_R8 => "d",
                    Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE or Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF or Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_MVAR => "i",
                    _ => throw new NotSupportedException(),
                };
            }
            return signature;
        }

        private void AddStruct(Il2CppTypeDefinition typeDef, int typeIndex)
        {
            var structInfo = new StructInfo();
            structInfoList.Add(structInfo);
            structInfo.TypeName = structNameDic[typeDef];
            structInfo.IsValueType = typeDef.IsValueType;
            structInfo.TypeDefIndex = typeIndex;
            AddParents(typeDef, structInfo);
            AddFields(typeDef, structInfo, null, typeIndex);
            AddVTableMethod(structInfo, typeDef);
            AddRGCTX(structInfo, typeDef);
        }

        private void AddGenericClassStruct(ulong pointer)
        {
            var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(pointer);
            var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
            if (typeDef.IsEnum)
            {
                return;
            }
            var structInfo = new StructInfo();
            structInfoList.Add(structInfo);
            structInfo.TypeName = genericClassStructNameDic[pointer];
            structInfo.IsValueType = typeDef.IsValueType;
            structInfo.TypeDefIndex = typeDefIndices.TryGetValue(typeDef, out var typeIndex) ? typeIndex : -1;
            AddParents(typeDef, structInfo);
            AddFields(typeDef, structInfo, genericClass.context, structInfo.TypeDefIndex);
            AddVTableMethod(structInfo, typeDef);
        }

        private void AddParents(Il2CppTypeDefinition typeDef, StructInfo structInfo)
        {
            if (!typeDef.IsValueType && !typeDef.IsEnum)
            {
                if (typeDef.parentIndex >= 0)
                {
                    var parent = il2Cpp.types[typeDef.parentIndex];
                    if (parent.type != Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)
                    {
                        structInfo.Parent = GetIl2CppStructName(parent);
                    }
                }
            }
        }

        private void AddEnum(Il2CppTypeDefinition typeDef)
        {
            var enumName = structNameDic[typeDef];
            Il2CppType underlyingType;
            try
            {
                underlyingType = executor.GetEnumUnderlyingType(typeDef);
            }
            catch
            {
                return;
            }
            var enumInfo = new EnumInfo
            {
                Name = enumName,
                UnderlyingTypeName = ParseType(underlyingType),
                UnderlyingSize = GetPrimitiveLayoutSize(underlyingType.type)
            };
            var usedValues = new HashSet<long>();
            if (typeDef.field_count > 0)
            {
                var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                {
                    var fieldDef = metadata.fieldDefs[i];
                    var fieldType = il2Cpp.types[fieldDef.typeIndex];
                    if ((fieldType.attrs & FIELD_ATTRIBUTE_LITERAL) == 0)
                    {
                        continue;
                    }
                    var rawName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                    if (rawName == "value__")
                    {
                        continue;
                    }
                    var memberName = GetUniqueEnumMemberName(enumName + "_" + FixName(rawName));
                    if (!metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefault) || fieldDefault.dataIndex == -1)
                    {
                        continue;
                    }
                    if (!executor.TryGetDefaultValue(fieldDefault.typeIndex, fieldDefault.dataIndex, out var value) || value == null)
                    {
                        continue;
                    }
                    long numeric;
                    try
                    {
                        numeric = Convert.ToInt64(value);
                    }
                    catch
                    {
                        continue;
                    }
                    if (!usedValues.Add(numeric))
                    {
                        continue;
                    }
                    enumInfo.Members.Add(new EnumMemberInfo
                    {
                        Name = memberName,
                        ValueLiteral = numeric.ToString(),
                        Value = numeric
                    });
                }
            }
            enumInfoList.Add(enumInfo);
            enumSizeByName[enumName] = enumInfo.UnderlyingSize;
        }

        private string GetUniqueEnumMemberName(string name)
        {
            var unique = name;
            var i = 1;
            while (!enumMemberNames.Add(unique))
            {
                unique = $"{name}_{i++}";
            }
            return unique;
        }

        private string BuildEnumHeader()
        {
            var sb = new StringBuilder();
            foreach (var enumInfo in enumInfoList)
            {
                if (enumInfo.Members.Count == 0)
                {
                    sb.Append($"typedef {enumInfo.UnderlyingTypeName} {enumInfo.Name};\n");
                    continue;
                }
                sb.Append($"typedef enum {enumInfo.Name}\n{{\n");
                for (int i = 0; i < enumInfo.Members.Count; i++)
                {
                    var member = enumInfo.Members[i];
                    sb.Append($"\t{member.Name} = {member.ValueLiteral}");
                    sb.Append(i == enumInfo.Members.Count - 1 ? "\n" : ",\n");
                }
                sb.Append($"}} {enumInfo.Name};\n");
            }
            return sb.ToString();
        }

        private void AddFields(Il2CppTypeDefinition typeDef, StructInfo structInfo, Il2CppGenericContext context, int typeIndex)
        {
            if (typeDef.field_count > 0)
            {
                var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                var cache = new HashSet<string>(StringComparer.Ordinal);
                for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                {
                    var fieldDef = metadata.fieldDefs[i];
                    var fieldType = il2Cpp.types[fieldDef.typeIndex];
                    if ((fieldType.attrs & FIELD_ATTRIBUTE_LITERAL) != 0)
                    {
                        continue;
                    }
                    var isEnumField = IsEnumType(fieldType, context);
                    var structFieldInfo = new StructFieldInfo
                    {
                        FieldTypeName = ParseType(fieldType, context),
                        IsEnum = isEnumField
                    };
                    if (isEnumField)
                    {
                        structFieldInfo.EnumTypeName = structFieldInfo.FieldTypeName;
                        structFieldInfo.LayoutSize = GetTypeLayoutSize(fieldType, context);
                        if (structFieldInfo.LayoutSize != 4)
                        {
                            structFieldInfo.FieldTypeName = GetEnumUnderlyingCType(fieldType, context);
                        }
                    }
                    else if (fieldType.type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN)
                    {
                        structFieldInfo.FieldTypeName = "uint8_t";
                        structFieldInfo.LayoutSize = 1;
                    }
                    else
                    {
                        structFieldInfo.LayoutSize = GetTypeLayoutSize(fieldType, context);
                    }
                    var fieldName = FixName(metadata.GetStringFromIndex(fieldDef.nameIndex));
                    if (!cache.Add(fieldName))
                    {
                        fieldName = $"_{i - typeDef.fieldStart}_{fieldName}";
                    }
                    structFieldInfo.FieldName = fieldName;
                    structFieldInfo.IsValueType = IsValueType(fieldType, context);
                    structFieldInfo.IsCustomType = IsCustomType(fieldType, context);
                    var isStatic = (fieldType.attrs & FIELD_ATTRIBUTE_STATIC) != 0;
                    if (typeIndex >= 0)
                    {
                        var rawOffset = il2Cpp.GetFieldOffsetFromIndex(typeIndex, i - typeDef.fieldStart, i, typeDef.IsValueType, isStatic);
                        structFieldInfo.Offset = ToRelativeFieldOffset(rawOffset, structInfo.IsValueType, isStatic);
                    }
                    if (isStatic)
                    {
                        structInfo.StaticFields.Add(structFieldInfo);
                    }
                    else
                    {
                        structInfo.Fields.Add(structFieldInfo);
                    }
                }
            }
            structInfo.UseExplicitLayout = structInfo.Fields.Count == 0 || structInfo.Fields.All(f => f.Offset >= 0);
            // Open-generic typeDefs report every instantiated field at the same offset (usually 0).
            // Sequential emit matches the real valuetype layout; explicit mode would comment later fields as OVERLAP.
            if (context != null && HasCollapsedFieldOffsets(structInfo.Fields))
            {
                structInfo.UseExplicitLayout = false;
            }
        }

        private static bool HasCollapsedFieldOffsets(List<StructFieldInfo> fields)
        {
            if (fields == null || fields.Count <= 1)
            {
                return false;
            }
            var first = fields[0].Offset;
            if (first < 0)
            {
                return false;
            }
            for (int i = 1; i < fields.Count; i++)
            {
                if (fields[i].Offset != first)
                {
                    return false;
                }
            }
            return true;
        }

        private int ToRelativeFieldOffset(int rawOffset, bool isValueType, bool isStatic)
        {
            if (rawOffset < 0)
            {
                return -1;
            }
            if (isStatic || isValueType)
            {
                return rawOffset;
            }
            var headerSize = ObjectHeaderSize;
            if (rawOffset < headerSize)
            {
                return -1;
            }
            return rawOffset - headerSize;
        }

        private int ObjectHeaderSize => il2Cpp.Is32Bit ? 8 : 16;

        private void AddVTableMethod(StructInfo structInfo, Il2CppTypeDefinition typeDef)
        {
            if (typeDef.vtable_count == 0 || metadata.vtableMethods == null)
            {
                return;
            }
            structInfo.VTableMethod = new StructVTableMethodInfo[typeDef.vtable_count];
            for (int i = 0; i < typeDef.vtable_count; i++)
            {
                var methodInfo = new StructVTableMethodInfo
                {
                    MethodName = "unknown"
                };
                structInfo.VTableMethod[i] = methodInfo;
                try
                {
                    var vTableIndex = typeDef.vtableStart + i;
                    if (vTableIndex < 0 || vTableIndex >= metadata.vtableMethods.Length)
                    {
                        continue;
                    }
                    var encodedMethodIndex = metadata.vtableMethods[vTableIndex];
                    if (encodedMethodIndex == 0)
                    {
                        methodInfo.MethodName = "empty";
                        continue;
                    }
                    var usage = Metadata.GetEncodedIndexType(encodedMethodIndex);
                    var index = metadata.GetDecodedMethodIndex(encodedMethodIndex);
                    if (usage == 6) //kIl2CppMetadataUsageMethodRef
                    {
                        if (index >= il2Cpp.methodSpecs.Length)
                        {
                            continue;
                        }
                        var methodSpec = il2Cpp.methodSpecs[index];
                        (_, var specMethodName) = executor.GetMethodSpecName(methodSpec, false);
                        methodInfo.MethodName = FixName(specMethodName);
                    }
                    else
                    {
                        if (index >= metadata.methodDefs.Length)
                        {
                            continue;
                        }
                        var methodDef = metadata.methodDefs[index];
                        methodInfo.MethodName = FixName(metadata.GetStringFromIndex(methodDef.nameIndex));
                    }
                }
                catch
                {
                    methodInfo.MethodName = "unknown";
                }
            }
        }

        private void AddRGCTX(StructInfo structInfo, Il2CppTypeDefinition typeDef)
        {
            var imageName = typeDefImageNames[typeDef];
            var collection = executor.GetRGCTXDefinition(imageName, typeDef);
            if (collection != null)
            {
                foreach (var definitionData in collection)
                {
                    var structRGCTXInfo = new StructRGCTXInfo();
                    structInfo.RGCTXs.Add(structRGCTXInfo);
                    structRGCTXInfo.Type = definitionData.type;
                    Il2CppRGCTXDefinitionData rgctxDefData;
                    if (il2Cpp.Version >= 27.2)
                    {
                        rgctxDefData = il2Cpp.MapVATR<Il2CppRGCTXDefinitionData>(definitionData._data);
                    }
                    else
                    {
                        rgctxDefData = definitionData.data;
                    }
                    switch (definitionData.type)
                    {
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE:
                            {
                                var il2CppType = il2Cpp.types[rgctxDefData.typeIndex];
                                structRGCTXInfo.TypeName = FixName(executor.GetTypeName(il2CppType, true, false));
                                break;
                            }
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS:
                            {
                                var il2CppType = il2Cpp.types[rgctxDefData.typeIndex];
                                structRGCTXInfo.ClassName = FixName(executor.GetTypeName(il2CppType, true, false));
                                break;
                            }
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD:
                            {
                                var methodSpec = il2Cpp.methodSpecs[rgctxDefData.methodIndex];
                                (var methodSpecTypeName, var methodSpecMethodName) = executor.GetMethodSpecName(methodSpec, true);
                                structRGCTXInfo.MethodName = FixName(methodSpecTypeName + "." + methodSpecMethodName);
                                break;
                            }
                    }
                }
            }
        }

        private List<StructRGCTXInfo> GenerateRGCTX(string imageName, Il2CppMethodDefinition methodDef)
        {
            var rgctxs = new List<StructRGCTXInfo>();
            var collection = executor.GetRGCTXDefinition(imageName, methodDef);
            if (collection != null)
            {
                foreach (var definitionData in collection)
                {
                    var structRGCTXInfo = new StructRGCTXInfo();
                    rgctxs.Add(structRGCTXInfo);
                    structRGCTXInfo.Type = definitionData.type;
                    Il2CppRGCTXDefinitionData rgctxDefData;
                    if (il2Cpp.Version >= 27.2)
                    {
                        rgctxDefData = il2Cpp.MapVATR<Il2CppRGCTXDefinitionData>(definitionData._data);
                    }
                    else
                    {
                        rgctxDefData = definitionData.data;
                    }
                    switch (definitionData.type)
                    {
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE:
                            {
                                var il2CppType = il2Cpp.types[rgctxDefData.typeIndex];
                                structRGCTXInfo.TypeName = FixName(executor.GetTypeName(il2CppType, true, false));
                                break;
                            }
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS:
                            {
                                var il2CppType = il2Cpp.types[rgctxDefData.typeIndex];
                                structRGCTXInfo.ClassName = FixName(executor.GetTypeName(il2CppType, true, false));
                                break;
                            }
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD:
                            {
                                var methodSpec = il2Cpp.methodSpecs[rgctxDefData.methodIndex];
                                (var methodSpecTypeName, var methodSpecMethodName) = executor.GetMethodSpecName(methodSpec, true);
                                structRGCTXInfo.MethodName = FixName(methodSpecTypeName + "." + methodSpecMethodName);
                                break;
                            }
                    }
                }
            }
            return rgctxs;
        }

        private void ParseArrayClassStruct(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            var structName = GetIl2CppStructName(il2CppType, context);
            arrayClassHeader.Append($"struct {structName}_array {{\n" +
                $"\tIl2CppObject obj;\n" +
                $"\tIl2CppArrayBounds *bounds;\n" +
                $"\til2cpp_array_size_t max_length;\n" +
                $"\t{ParseType(il2CppType, context)} m_Items[65535];\n" +
                $"}};\n");
        }

        private Il2CppTypeDefinition GetTypeDefinition(Il2CppType il2CppType)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    return executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                    return executor.GetGenericClassTypeDefinition(genericClass);
                default:
                    throw new NotSupportedException();
            }
        }

        private void CreateStructNameDic(Il2CppTypeDefinition typeDef)
        {
            var typeName = executor.GetTypeDefName(typeDef, true, true);
            var typeStructName = FixName(typeName);
            var uniqueName = GetUniqueName(typeStructName);
            structNameDic.Add(typeDef, uniqueName);
        }

        private string GetUniqueName(string name)
        {
            var fixName = name;
            int i = 1;
            while (!structNameHashSet.Add(fixName))
            {
                fixName = $"{name}_{i++}";
            }
            return fixName;
        }

        private string RecursionStructInfo(StructInfo info)
        {
            if (!structCache.Add(info))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var pre = new StringBuilder();
            var embedParent = FindEmittableLayoutParent(info);

            sb.Append($"struct {info.TypeName}_Fields {{\n");
            if (!string.IsNullOrEmpty(embedParent))
            {
                var parentStructName = embedParent + "_o";
                if (structInfoWithStructName.TryGetValue(parentStructName, out var parentInfo))
                {
                    pre.Append(RecursionStructInfo(parentInfo));
                }
                sb.Append($"\tstruct {embedParent}_Fields _;\n");
            }
            AppendLayoutFields(sb, pre, info.Fields, info, embedParent, true);
            sb.Append("};\n");

            if (info.RGCTXs.Count > 0)
            {
                sb.Append($"struct {info.TypeName}_RGCTXs {{\n");
                for (int i = 0; i < info.RGCTXs.Count; i++)
                {
                    var rgctx = info.RGCTXs[i];
                    switch (rgctx.Type)
                    {
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE:
                            sb.Append($"\tIl2CppType* _{i}_{rgctx.TypeName};\n");
                            break;
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS:
                            sb.Append($"\tIl2CppClass* _{i}_{rgctx.ClassName};\n");
                            break;
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD:
                            sb.Append($"\tMethodInfo* _{i}_{rgctx.MethodName};\n");
                            break;
                    }
                }
                sb.Append("};\n");
            }

            if (info.VTableMethod.Length > 0)
            {
                sb.Append($"struct {info.TypeName}_VTable {{\n");
                for (int i = 0; i < info.VTableMethod.Length; i++)
                {
                    sb.Append($"\tVirtualInvokeData _{i}_");
                    var method = info.VTableMethod[i];
                    if (method != null)
                    {
                        sb.Append(method.MethodName);
                    }
                    else
                    {
                        sb.Append("unknown");
                    }
                    sb.Append(";\n");
                }
                sb.Append("};\n");
            }

            sb.Append($"struct {info.TypeName}_c {{\n");
            sb.Append($"\tIl2CppClass_1 _1;\n");
            if (info.StaticFields.Count > 0)
            {
                sb.Append($"\tstruct {info.TypeName}_StaticFields* static_fields;\n");
            }
            else
            {
                sb.Append("\tvoid* static_fields;\n");
            }
            if (info.RGCTXs.Count > 0)
            {
                sb.Append($"\t{info.TypeName}_RGCTXs* rgctx_data;\n");
            }
            else
            {
                sb.Append("\tIl2CppRGCTXData* rgctx_data;\n");
            }
            sb.Append($"\tIl2CppClass_2 _2;\n");
            if (info.VTableMethod.Length > 0)
            {
                sb.Append($"\t{info.TypeName}_VTable vtable;\n");
            }
            else
            {
                sb.Append("\tVirtualInvokeData vtable[32];\n");
            }
            sb.Append($"}};\n");

            sb.Append($"struct {info.TypeName}_o {{\n");
            if (!info.IsValueType)
            {
                sb.Append($"\t{info.TypeName}_c *klass;\n");
                sb.Append($"\tvoid *monitor;\n");
            }
            sb.Append($"\t{info.TypeName}_Fields fields;\n");
            sb.Append("};\n");

            if (info.StaticFields.Count > 0)
            {
                sb.Append($"struct {info.TypeName}_StaticFields {{\n");
                AppendLayoutFields(sb, pre, info.StaticFields, info, null, false);
                sb.Append("};\n");
            }

            return pre.Append(sb).ToString();
        }

        private void AppendLayoutFields(StringBuilder sb, StringBuilder pre, List<StructFieldInfo> fields, StructInfo owner, string embedParent, bool isInstance)
        {
            var useExplicit = isInstance ? owner.UseExplicitLayout : fields.Count == 0 || fields.All(f => f.Offset >= 0);
            if (!isInstance && HasCollapsedFieldOffsets(fields))
            {
                useExplicit = false;
            }
            uint currentOffset = 0;
            if (isInstance && !string.IsNullOrEmpty(embedParent) && structInfoWithStructName.TryGetValue(embedParent + "_o", out var parentInfo))
            {
                currentOffset = parentInfo.FieldsSize;
            }

            IEnumerable<StructFieldInfo> ordered = useExplicit
                ? fields.OrderBy(f => f.Offset < 0 ? int.MaxValue : f.Offset)
                : fields;

            var fieldList = ordered.ToList();
            for (int k = 0; k < fieldList.Count; k++)
            {
                var field = fieldList[k];
                EnsureValueTypeEmitted(pre, field);
                var size = ResolveFieldLayoutSize(field);
                if (useExplicit && field.Offset >= 0)
                {
                    if ((uint)field.Offset < currentOffset)
                    {
                        sb.Append($"\t// OVERLAP: {field.FieldTypeName} {field.FieldName}; // 0x{field.Offset:X}\n");
                        continue;
                    }
                    if ((uint)field.Offset > currentOffset)
                    {
                        var pad = (uint)field.Offset - currentOffset;
                        sb.Append($"\tchar _padding_{currentOffset:X}[0x{pad:X}];\n");
                        currentOffset = (uint)field.Offset;
                    }
                    if (size == 0 && k < fieldList.Count - 1 && fieldList[k + 1].Offset > field.Offset)
                    {
                        size = (uint)(fieldList[k + 1].Offset - field.Offset);
                    }
                    if (size == 0)
                    {
                        size = (uint)il2Cpp.PointerSize;
                    }
                    currentOffset += size;
                }
                AppendFieldDeclaration(sb, field);
            }
        }

        private void EnsureValueTypeEmitted(StringBuilder pre, StructFieldInfo field)
        {
            if (!field.IsValueType || string.IsNullOrEmpty(field.FieldTypeName))
            {
                return;
            }
            if (structInfoWithStructName.TryGetValue(field.FieldTypeName, out var fieldInfo))
            {
                pre.Append(RecursionStructInfo(fieldInfo));
            }
        }

        private void AppendFieldDeclaration(StringBuilder sb, StructFieldInfo field)
        {
            if (field.IsEnum)
            {
                if (field.LayoutSize == 4)
                {
                    sb.Append($"\t{field.FieldTypeName} {field.FieldName};\n");
                }
                else if (!string.IsNullOrEmpty(field.EnumTypeName))
                {
                    sb.Append($"\t{field.FieldTypeName} {field.FieldName}; // enum {field.EnumTypeName}\n");
                }
                else
                {
                    sb.Append($"\t{field.FieldTypeName} {field.FieldName};\n");
                }
                return;
            }
            if (field.IsValueType)
            {
                var structName = field.FieldTypeName;
                if (structName.EndsWith("_o", StringComparison.Ordinal))
                {
                    structName = structName[..^2];
                }
                sb.Append($"\tstruct {structName}_Fields {field.FieldName};\n");
                return;
            }
            if (field.IsCustomType)
            {
                sb.Append($"\tstruct {field.FieldTypeName} {field.FieldName};\n");
            }
            else
            {
                sb.Append($"\t{field.FieldTypeName} {field.FieldName};\n");
            }
        }

        private bool TryResolveGenericParameterType(Il2CppType il2CppType, Il2CppGenericContext context, bool methodGeneric, out Il2CppType type)
        {
            type = null;
            if (context == null)
            {
                return false;
            }
            try
            {
                var genericParameter = executor.GetGenericParameteFromIl2CppType(il2CppType);
                var instPointer = methodGeneric ? context.method_inst : context.class_inst;
                if (instPointer == 0 && methodGeneric)
                {
                    instPointer = context.class_inst;
                }
                if (genericParameter == null || instPointer == 0)
                {
                    return false;
                }
                var genericInst = il2Cpp.MapVATR<Il2CppGenericInst>(instPointer);
                if (genericInst.type_argc <= 0 || genericInst.type_argc > MaxGenericArgumentCount ||
                    genericParameter.num >= genericInst.type_argc || genericInst.type_argv == 0)
                {
                    return false;
                }
                var typeArgvOffset = il2Cpp.MapVATR(genericInst.type_argv);
                var pointerOffset = typeArgvOffset + (ulong)genericParameter.num * il2Cpp.PointerSize;
                if (pointerOffset + il2Cpp.PointerSize > il2Cpp.Length)
                {
                    return false;
                }
                il2Cpp.Position = pointerOffset;
                var pointer = il2Cpp.ReadUIntPtr();
                type = il2Cpp.GetIl2CppType(pointer);
                return type != null;
            }
            catch
            {
                return false;
            }
        }

        private string GetIl2CppStructName(Il2CppType il2CppType, Il2CppGenericContext context = null)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        return structNameDic[typeDef];
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return GetIl2CppStructName(oriType);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        var elementType = il2Cpp.GetIl2CppType(arrayType.etype);
                        var elementStructName = GetIl2CppStructName(elementType, context);
                        var typeStructName = elementStructName + "_array";
                        if (structNameHashSet.Add(typeStructName))
                        {
                            ParseArrayClassStruct(elementType, context);
                        }
                        return typeStructName;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        var elementType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        var elementStructName = GetIl2CppStructName(elementType, context);
                        var typeStructName = elementStructName + "_array";
                        if (structNameHashSet.Add(typeStructName))
                        {
                            ParseArrayClassStruct(elementType, context);
                        }
                        return typeStructName;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var typeStructName = genericClassStructNameDic[il2CppType.data.generic_class];
                        if (structNameHashSet.Add(typeStructName))
                        {
                            genericClassList.Add(il2CppType.data.generic_class);
                        }
                        return typeStructName;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                        {
                            return GetIl2CppStructName(type);
                        }
                        return "System_Object";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, true, out var type))
                        {
                            return GetIl2CppStructName(type);
                        }
                        return "System_Object";
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        private bool IsValueType(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        return !typeDef.IsEnum;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        return typeDef.IsValueType && !typeDef.IsEnum;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                        {
                            return IsValueType(type, null);
                        }
                        return false;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, true, out var type))
                        {
                            return IsValueType(type, null);
                        }
                        return false;
                    }
                default:
                    return false;
            }
        }

        private bool IsCustomType(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return IsCustomType(oriType, context);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        return true;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        if (typeDef.IsEnum)
                        {
                            return IsCustomType(il2Cpp.types[typeDef.elementTypeIndex], context);
                        }
                        return true;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        if (typeDef.IsEnum)
                        {
                            return IsCustomType(il2Cpp.types[typeDef.elementTypeIndex], context);
                        }
                        return true;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                        {
                            return IsCustomType(type, null);
                        }
                        return false;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        if (TryResolveGenericParameterType(il2CppType, context, true, out var type))
                        {
                            return IsCustomType(type, null);
                        }
                        return false;
                    }
                default:
                    return false;
            }
        }

        private void GenerateMethodInfo(string methodInfoName, string structTypeName, List<StructRGCTXInfo> rgctxs)
        {
            if (rgctxs.Count > 0)
            {
                methodInfoHeader.Append($"struct {methodInfoName}_RGCTXs {{\n");
                for (int i = 0; i < rgctxs.Count; i++)
                {
                    var rgctx = rgctxs[i];
                    switch (rgctx.Type)
                    {
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE:
                            methodInfoHeader.Append($"\tIl2CppType* _{i}_{rgctx.TypeName};\n");
                            break;
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS:
                            methodInfoHeader.Append($"\tIl2CppClass* _{i}_{rgctx.ClassName};\n");
                            break;
                        case Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD:
                            methodInfoHeader.Append($"\tMethodInfo* _{i}_{rgctx.MethodName};\n");
                            break;
                    }
                }
                methodInfoHeader.Append("};\n");
            }

            methodInfoHeader.Append($"struct {methodInfoName} {{\n");
            methodInfoHeader.Append($"\tIl2CppMethodPointer methodPointer;\n");
            if (il2Cpp.Version >= 29)
            {
                methodInfoHeader.Append($"\tIl2CppMethodPointer virtualMethodPointer;\n");
                methodInfoHeader.Append($"\tInvokerMethod invoker_method;\n");
            }
            else
            {
                methodInfoHeader.Append($"\tvoid* invoker_method;\n"); //TODO
            }
            methodInfoHeader.Append($"\tconst char* name;\n");
            if (il2Cpp.Version <= 24)
            {
                methodInfoHeader.Append($"\t{structTypeName}_c *declaring_type;\n");
            }
            else
            {
                methodInfoHeader.Append($"\t{structTypeName}_c *klass;\n");
            }
            methodInfoHeader.Append($"\tconst Il2CppType *return_type;\n");
            if (il2Cpp.Version >= 29)
            {
                methodInfoHeader.Append($"\tconst Il2CppType** parameters;\n");
            }
            else
            {
                methodInfoHeader.Append($"\tconst void* parameters;\n"); //ParameterInfo*
            }
            if (rgctxs.Count > 0)
            {
                methodInfoHeader.Append($"\tconst {methodInfoName}_RGCTXs* rgctx_data;\n");
            }
            else
            {
                methodInfoHeader.Append($"\tconst Il2CppRGCTXData* rgctx_data;\n");
            }
            methodInfoHeader.Append($"\tunion\n");
            methodInfoHeader.Append($"\t{{\n");
            methodInfoHeader.Append($"\t\tconst void* genericMethod;\n");
            if (il2Cpp.Version >= 27)
            {
                methodInfoHeader.Append($"\t\tconst void* genericContainerHandle;\n");
            }
            else
            {
                methodInfoHeader.Append($"\t\tconst void* genericContainer;\n");
            }
            methodInfoHeader.Append($"\t}};\n");
            if (il2Cpp.Version <= 24)
            {
                methodInfoHeader.Append($"\tint32_t customAttributeIndex;\n");
            }
            methodInfoHeader.Append($"\tuint32_t token;\n");
            methodInfoHeader.Append($"\tuint16_t flags;\n");
            methodInfoHeader.Append($"\tuint16_t iflags;\n");
            methodInfoHeader.Append($"\tuint16_t slot;\n");
            methodInfoHeader.Append($"\tuint8_t parameters_count;\n");
            methodInfoHeader.Append($"\tuint8_t bitflags;\n");
            methodInfoHeader.Append($"}};\n");
        }

        private void ComputeAllFieldsSizes()
        {
            var visiting = new HashSet<StructInfo>();
            foreach (var info in structInfoList)
            {
                ComputeFieldsSize(info, visiting);
            }
        }

        private uint ComputeFieldsSize(StructInfo info, HashSet<StructInfo> visiting)
        {
            if (info.FieldsSizeComputed)
            {
                return info.FieldsSize;
            }
            if (!visiting.Add(info))
            {
                return info.FieldsSize;
            }
            uint size = 0;
            var parentNameForSize = FindEmittableLayoutParent(info);
            if (!string.IsNullOrEmpty(parentNameForSize) &&
                structInfoWithStructName.TryGetValue(parentNameForSize + "_o", out var parentInfo))
            {
                size = ComputeFieldsSize(parentInfo, visiting);
            }
            if (info.UseExplicitLayout)
            {
                foreach (var field in info.Fields)
                {
                    if (field.Offset < 0)
                    {
                        continue;
                    }
                    var fieldSize = ResolveFieldLayoutSize(field, visiting);
                    var end = (uint)field.Offset + fieldSize;
                    if (end > size)
                    {
                        size = end;
                    }
                }
            }
            else
            {
                foreach (var field in info.Fields)
                {
                    size += ResolveFieldLayoutSize(field, visiting);
                }
            }
            info.FieldsSize = size;
            info.FieldsSizeComputed = true;
            visiting.Remove(info);
            return size;
        }

        private uint ResolveFieldLayoutSize(StructFieldInfo field)
        {
            return ResolveFieldLayoutSize(field, null);
        }

        private uint ResolveFieldLayoutSize(StructFieldInfo field, HashSet<StructInfo> visiting)
        {
            if (field.LayoutSize > 0)
            {
                return field.LayoutSize;
            }
            if (field.IsEnum && !string.IsNullOrEmpty(field.EnumTypeName) &&
                enumSizeByName.TryGetValue(field.EnumTypeName, out var enumSize))
            {
                field.LayoutSize = enumSize;
                return enumSize;
            }
            if (field.IsValueType && structInfoWithStructName.TryGetValue(field.FieldTypeName, out var nested))
            {
                var nestedSize = visiting == null ? nested.FieldsSize : ComputeFieldsSize(nested, visiting);
                if (nestedSize > 0)
                {
                    field.LayoutSize = nestedSize;
                }
                return nestedSize;
            }
            if (!string.IsNullOrEmpty(field.FieldTypeName) && field.FieldTypeName.EndsWith("*", StringComparison.Ordinal))
            {
                return (uint)il2Cpp.PointerSize;
            }
            return GetCTypeLayoutSize(field.FieldTypeName);
        }

        private uint GetCTypeLayoutSize(string typeName)
        {
            return typeName switch
            {
                "uint8_t" or "int8_t" or "bool" => 1,
                "uint16_t" or "int16_t" => 2,
                "uint32_t" or "int32_t" or "float" => 4,
                "uint64_t" or "int64_t" or "double" => 8,
                "intptr_t" or "uintptr_t" => (uint)il2Cpp.PointerSize,
                _ => (uint)il2Cpp.PointerSize
            };
        }

        private string FindEmittableLayoutParent(StructInfo info)
        {
            var parent = info.Parent;
            var guard = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(parent) && guard.Add(parent))
            {
                if (structInfoWithStructName.TryGetValue(parent + "_o", out var parentInfo) &&
                    HasLayoutFields(parentInfo, new HashSet<StructInfo>()))
                {
                    return parent;
                }
                if (parentInfo == null)
                {
                    break;
                }
                parent = parentInfo.Parent;
            }
            return null;
        }

        private bool HasLayoutFields(StructInfo info, HashSet<StructInfo> visiting)
        {
            if (info.Fields.Count > 0)
            {
                return true;
            }
            if (!visiting.Add(info))
            {
                return false;
            }
            if (string.IsNullOrEmpty(info.Parent))
            {
                return false;
            }
            if (structInfoWithStructName.TryGetValue(info.Parent + "_o", out var parentInfo))
            {
                return HasLayoutFields(parentInfo, visiting);
            }
            return false;
        }

        private bool IsEnumType(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        return typeDef != null && typeDef.IsEnum;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        return typeDef != null && typeDef.IsEnum;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        return TryResolveGenericParameterType(il2CppType, context, false, out var type) &&
                               IsEnumType(type, null);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        return TryResolveGenericParameterType(il2CppType, context, true, out var type) &&
                               IsEnumType(type, null);
                    }
                default:
                    return false;
            }
        }

        private string GetEnumUnderlyingCType(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            var underlying = GetEnumUnderlyingIl2CppType(il2CppType, context);
            return underlying == null ? "int32_t" : ParseType(underlying);
        }

        private Il2CppType GetEnumUnderlyingIl2CppType(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            try
            {
                switch (il2CppType.type)
                {
                    case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                        return executor.GetEnumUnderlyingType(executor.GetTypeDefinitionFromIl2CppType(il2CppType));
                    case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                        {
                            var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                            return executor.GetEnumUnderlyingType(executor.GetGenericClassTypeDefinition(genericClass));
                        }
                    case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                        if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                        {
                            return GetEnumUnderlyingIl2CppType(type, null);
                        }
                        break;
                    case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                        if (TryResolveGenericParameterType(il2CppType, context, true, out var type2))
                        {
                            return GetEnumUnderlyingIl2CppType(type2, null);
                        }
                        break;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        private uint GetTypeLayoutSize(Il2CppType il2CppType, Il2CppGenericContext context)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return 1;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return 2;
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return 4;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return 8;
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return (uint)il2Cpp.PointerSize;
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                    return (uint)il2Cpp.PointerSize;
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        if (typeDef != null && typeDef.IsEnum)
                        {
                            return GetPrimitiveLayoutSize(executor.GetEnumUnderlyingType(typeDef).type);
                        }
                        return 0;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        if (typeDef != null && typeDef.IsEnum)
                        {
                            return GetPrimitiveLayoutSize(executor.GetEnumUnderlyingType(typeDef).type);
                        }
                        if (typeDef != null && !typeDef.IsValueType)
                        {
                            return (uint)il2Cpp.PointerSize;
                        }
                        return 0;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    if (TryResolveGenericParameterType(il2CppType, context, false, out var type))
                    {
                        return GetTypeLayoutSize(type, null);
                    }
                    return (uint)il2Cpp.PointerSize;
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    if (TryResolveGenericParameterType(il2CppType, context, true, out var type2))
                    {
                        return GetTypeLayoutSize(type2, null);
                    }
                    return (uint)il2Cpp.PointerSize;
                default:
                    return (uint)il2Cpp.PointerSize;
            }
        }

        private static uint GetPrimitiveLayoutSize(Il2CppTypeEnum type)
        {
            return type switch
            {
                Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
                Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
                Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
                Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or Il2CppTypeEnum.IL2CPP_TYPE_R8 => 8,
                _ => 4
            };
        }
    }
}
