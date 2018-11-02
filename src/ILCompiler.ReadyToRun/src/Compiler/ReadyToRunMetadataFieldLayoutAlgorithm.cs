// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using ILCompiler.DependencyAnalysis.ReadyToRun;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace ILCompiler
{
    internal class ReadyToRunMetadataFieldLayoutAlgorithm : MetadataFieldLayoutAlgorithm
    {
        /// <summary>
        /// Map from EcmaModule instances to field layouts within the individual modules.
        /// </summary>
        private ModuleFieldLayoutMap _moduleFieldLayoutMap;

        public ReadyToRunMetadataFieldLayoutAlgorithm()
        {
            _moduleFieldLayoutMap = new ModuleFieldLayoutMap();
        }

        public override ComputedStaticFieldLayout ComputeStaticFieldLayout(DefType defType, StaticLayoutKind layoutKind)
        {
            ComputedStaticFieldLayout layout = new ComputedStaticFieldLayout();
            if (defType is EcmaType ecmaType)
            {
                // ECMA types are the only ones that can have statics
                ModuleFieldLayout moduleFieldLayout = _moduleFieldLayoutMap.GetOrCreateValue(ecmaType.EcmaModule);
                layout.GcStatics = moduleFieldLayout.GcStatics;
                layout.NonGcStatics = moduleFieldLayout.NonGcStatics;
                layout.ThreadGcStatics = moduleFieldLayout.ThreadGcStatics;
                layout.ThreadNonGcStatics = moduleFieldLayout.ThreadNonGcStatics;
                moduleFieldLayout.TypeToFieldMap.TryGetValue(ecmaType.Handle, out layout.Offsets);
            }
            return layout;
        }

        /// <summary>
        /// Map from modules to their static field layouts.
        /// </summary>
        private class ModuleFieldLayoutMap : LockFreeReaderHashtable<EcmaModule, ModuleFieldLayout>
        {
            /// <summary>
            /// CoreCLR DomainLocalModule::OffsetOfDataBlob() / sizeof(void *)
            /// </summary>
            private const int DomainLocalModuleDataBlobOffsetAsIntPtrCount = 6;

            /// <summary>
            /// CoreCLR ThreadLocalModule::OffsetOfDataBlob() / sizeof(void *)
            /// </summary>
            private const int ThreadLocalModuleDataBlobOffsetAsIntPtrCount = 3;

            protected override bool CompareKeyToValue(EcmaModule key, ModuleFieldLayout value)
            {
                return key == value.Module;
            }

            protected override bool CompareValueToValue(ModuleFieldLayout value1, ModuleFieldLayout value2)
            {
                return value1.Module == value2.Module;
            }

            protected override ModuleFieldLayout CreateValueFromKey(EcmaModule module)
            {
                int typeCountInModule = module.MetadataReader.GetTableRowCount(TableIndex.TypeDef);
                int pointerSize = module.Context.Target.PointerSize;

                // 0 corresponds to "normal" statics, 1 to thread-local statics
                LayoutInt[] gcStatics = new LayoutInt[2]
                {
                    LayoutInt.Zero,
                    LayoutInt.Zero
                };
                LayoutInt[] nonGcStatics = new LayoutInt[2]
                {
                    new LayoutInt(DomainLocalModuleDataBlobOffsetAsIntPtrCount * pointerSize + typeCountInModule),
                    new LayoutInt(ThreadLocalModuleDataBlobOffsetAsIntPtrCount * pointerSize + typeCountInModule),
                };
                Dictionary<TypeDefinitionHandle, FieldAndOffset[]> typeToFieldMap = new Dictionary<TypeDefinitionHandle, FieldAndOffset[]>();

                foreach (TypeDefinitionHandle typeDefHandle in module.MetadataReader.TypeDefinitions)
                {
                    TypeDefinition typeDef = module.MetadataReader.GetTypeDefinition(typeDefHandle);
                    List<FieldAndOffset> fieldsForType = null;
                    if (typeDef.GetGenericParameters().Count != 0)
                    {
                        // Generic types are exempt from the static field layout algorithm, see
                        // <a href="https://github.com/dotnet/coreclr/blob/659af58047a949ed50d11101708538d2e87f2568/src/vm/ceeload.cpp#L2049">this check</a>.
                        continue;
                    }
                    foreach (FieldDefinitionHandle fieldDefHandle in typeDef.GetFields())
                    {
                        FieldDefinition fieldDef = module.MetadataReader.GetFieldDefinition(fieldDefHandle);
                        if ((fieldDef.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) == FieldAttributes.Static)
                        {
                            int index = (IsFieldThreadStatic(in fieldDef, module.MetadataReader) ? 1 : 0);
                            int alignment = 1;
                            int size = 0;
                            bool isGcField = false;

                            CorElementType corElementType;
                            EntityHandle valueTypeHandle;

                            GetFieldElementTypeAndValueTypeHandle(in fieldDef, module.MetadataReader, out corElementType, out valueTypeHandle);
                            FieldDesc fieldDesc = module.GetField(fieldDefHandle);

                            switch (corElementType)
                            {
                                case CorElementType.ELEMENT_TYPE_I1:
                                case CorElementType.ELEMENT_TYPE_U1:
                                case CorElementType.ELEMENT_TYPE_BOOLEAN:
                                    size = 1;
                                    break;

                                case CorElementType.ELEMENT_TYPE_I2:
                                case CorElementType.ELEMENT_TYPE_U2:
                                case CorElementType.ELEMENT_TYPE_CHAR:
                                    alignment = 2;
                                    size = 2;
                                    break;

                                case CorElementType.ELEMENT_TYPE_I4:
                                case CorElementType.ELEMENT_TYPE_U4:
                                case CorElementType.ELEMENT_TYPE_R4:
                                    alignment = 4;
                                    size = 4;
                                    break;

                                case CorElementType.ELEMENT_TYPE_FNPTR:
                                case CorElementType.ELEMENT_TYPE_PTR:
                                case CorElementType.ELEMENT_TYPE_I:
                                case CorElementType.ELEMENT_TYPE_U:
                                    alignment = pointerSize;
                                    size = pointerSize;
                                    break;

                                case CorElementType.ELEMENT_TYPE_I8:
                                case CorElementType.ELEMENT_TYPE_U8:
                                case CorElementType.ELEMENT_TYPE_R8:
                                    alignment = 8;
                                    size = 8;
                                    break;

                                case CorElementType.ELEMENT_TYPE_VAR:
                                case CorElementType.ELEMENT_TYPE_MVAR:
                                case CorElementType.ELEMENT_TYPE_STRING:
                                case CorElementType.ELEMENT_TYPE_SZARRAY:
                                case CorElementType.ELEMENT_TYPE_ARRAY:
                                case CorElementType.ELEMENT_TYPE_CLASS:
                                case CorElementType.ELEMENT_TYPE_OBJECT:
                                    isGcField = true;
                                    alignment = pointerSize;
                                    size = pointerSize;
                                    break;

                                case CorElementType.ELEMENT_TYPE_BYREF:
                                    ThrowHelper.ThrowTypeLoadException(ExceptionStringID.ClassLoadGeneral, fieldDesc.OwningType);
                                    break;

                                // Statics for valuetypes where the valuetype is defined in this module are handled here. 
                                // Other valuetype statics utilize the pessimistic model below.
                                case CorElementType.ELEMENT_TYPE_VALUETYPE:
                                    isGcField = true;
                                    alignment = pointerSize;
                                    size = pointerSize;
                                    if (IsTypeByRefLike(valueTypeHandle, module.MetadataReader))
                                    {
                                        ThrowHelper.ThrowTypeLoadException(ExceptionStringID.ClassLoadGeneral, fieldDesc.OwningType);
                                    }
                                    break;

                                case CorElementType.ELEMENT_TYPE_END:
                                default:
                                    isGcField = true;
                                    alignment = pointerSize;
                                    size = pointerSize;
                                    if (!valueTypeHandle.IsNil)
                                    {
                                        // Allocate pessimistic non-GC area for cross-module fields as that's what CoreCLR does
                                        // <a href="https://github.com/dotnet/coreclr/blob/659af58047a949ed50d11101708538d2e87f2568/src/vm/ceeload.cpp#L2124">here</a>
                                        nonGcStatics[index] = LayoutInt.AlignUp(nonGcStatics[index], new LayoutInt(TargetDetails.MaximumPrimitiveSize))
                                            + new LayoutInt(TargetDetails.MaximumPrimitiveSize);
                                    }
                                    else
                                    {
                                        // Field has an unexpected type
                                        throw new InvalidProgramException();
                                    }
                                    break;
                            }

                            LayoutInt[] layout = (isGcField ? gcStatics : nonGcStatics);
                            LayoutInt offset = LayoutInt.AlignUp(layout[index], new LayoutInt(alignment));
                            layout[index] = offset + new LayoutInt(size);
                            if (fieldsForType == null)
                            {
                                fieldsForType = new List<FieldAndOffset>();
                            }
                            fieldsForType.Add(new FieldAndOffset(fieldDesc, offset));
                        }
                    }

                    if (fieldsForType != null)
                    {
                        typeToFieldMap.Add(typeDefHandle, fieldsForType.ToArray());
                    }
                }

                LayoutInt blockAlignment = new LayoutInt(TargetDetails.MaximumPrimitiveSize);

                return new ModuleFieldLayout(
                    module,
                    gcStatics: new StaticsBlock() { Size = gcStatics[0], LargestAlignment = blockAlignment },
                    nonGcStatics: new StaticsBlock() { Size = nonGcStatics[0], LargestAlignment = blockAlignment },
                    threadGcStatics: new StaticsBlock() { Size = gcStatics[1], LargestAlignment = blockAlignment },
                    threadNonGcStatics: new StaticsBlock() { Size = nonGcStatics[1], LargestAlignment = blockAlignment },
                    typeToFieldMap: typeToFieldMap);
            }

            protected override int GetKeyHashCode(EcmaModule key)
            {
                return key.GetHashCode();
            }

            protected override int GetValueHashCode(ModuleFieldLayout value)
            {
                return value.Module.GetHashCode();
            }

            /// <summary>
            /// Try to locate the ThreadStatic custom attribute on the field (much like EcmaField.cs does in the method InitializeFieldFlags).
            /// </summary>
            /// <param name="fieldDef">Field definition</param>
            /// <param name="metadataReader">Metadata reader for the module</param>
            /// <returns>true when the field is marked with the ThreadStatic custom attribute</returns>
            private static bool IsFieldThreadStatic(in FieldDefinition fieldDef, MetadataReader metadataReader)
            {
                return !metadataReader.GetCustomAttributeHandle(fieldDef.GetCustomAttributes(), "System", "ThreadStaticAttribute").IsNil;
            }

            /// <summary>
            /// Try to locate the IsByRefLike attribute on the type (much like EcmaType does in ComputeTypeFlags).
            /// </summary>
            /// <param name="typeDefHandle">Handle to the field type to analyze</param>
            /// <param name="metadataReader">Metadata reader for the active module</param>
            /// <returns></returns>
            private static bool IsTypeByRefLike(EntityHandle typeDefHandle, MetadataReader metadataReader)
            {
                return typeDefHandle.Kind == HandleKind.TypeDefinition &&
                    !metadataReader.GetCustomAttributeHandle(
                        metadataReader.GetTypeDefinition((TypeDefinitionHandle)typeDefHandle).GetCustomAttributes(),
                        "System.Runtime.CompilerServices",
                        "IsByRefLikeAttribute").IsNil;
            }

            /// <summary>
            /// Partially decode field signature to obtain CorElementType and optionally the type handle for VALUETYPE fields.
            /// </summary>
            /// <param name="fieldDef">Metadata field definition</param>
            /// <param name="metadataReader">Metadata reader for the active module</param>
            /// <param name="corElementType">Output element type decoded from the signature</param>
            /// <param name="valueTypeHandle">Value type handle decoded from the signature</param>
            private static void GetFieldElementTypeAndValueTypeHandle(
                in FieldDefinition fieldDef,
                MetadataReader metadataReader,
                out CorElementType corElementType,
                out EntityHandle valueTypeHandle)
            {
                BlobReader signature = metadataReader.GetBlobReader(fieldDef.Signature);
                SignatureHeader signatureHeader = signature.ReadSignatureHeader();
                if (signatureHeader.Kind != SignatureKind.Field)
                {
                    throw new InvalidProgramException();
                }

                corElementType = ReadElementType(ref signature);
                valueTypeHandle = default(EntityHandle);
                if (corElementType == CorElementType.ELEMENT_TYPE_GENERICINST)
                {
                    corElementType = ReadElementType(ref signature);
                }

                if (corElementType == CorElementType.ELEMENT_TYPE_VALUETYPE)
                {
                    valueTypeHandle = signature.ReadTypeHandle();
                }
            }

            /// <summary>
            /// Extract element type from a field signature after skipping various modifiers.
            /// </summary>
            /// <param name="signature">Signature byte array</param>
            /// <param name="index">On input, index into the signature array. Gets modified to point after the element type on return.</param>
            /// <returns></returns>
            private static CorElementType ReadElementType(ref BlobReader signature)
            {
                // SigParser::PeekElemType
                byte signatureByte = signature.ReadByte();
                if (signatureByte < (byte)CorElementType.ELEMENT_TYPE_CMOD_REQD)
                {
                    // Fast path
                    return (CorElementType)signatureByte;
                }

                // SigParser::SkipCustomModifiers -> SkipAnyVASentinel
                if (signatureByte == (byte)CorElementType.ELEMENT_TYPE_SENTINEL)
                {
                    signatureByte = signature.ReadByte();
                }

                // SigParser::SkipCustomModifiers - modifier loop
                while (signatureByte == (byte)CorElementType.ELEMENT_TYPE_CMOD_REQD ||
                    signatureByte == (byte)CorElementType.ELEMENT_TYPE_CMOD_OPT)
                {
                    signature.ReadCompressedInteger();
                    signatureByte = signature.ReadByte();
                }
                return (CorElementType)signatureByte;
            }
        }

        /// <summary>
        /// Field layouts for a given EcmaModule.
        /// </summary>
        private class ModuleFieldLayout
        {
            public EcmaModule Module { get; }

            public StaticsBlock GcStatics { get; }

            public StaticsBlock NonGcStatics { get;  }

            public StaticsBlock ThreadGcStatics { get;  }

            public StaticsBlock ThreadNonGcStatics { get;  }

            public IReadOnlyDictionary<TypeDefinitionHandle, FieldAndOffset[]> TypeToFieldMap { get; }

            public ModuleFieldLayout(
                EcmaModule module, 
                StaticsBlock gcStatics, 
                StaticsBlock nonGcStatics, 
                StaticsBlock threadGcStatics, 
                StaticsBlock threadNonGcStatics,
                IReadOnlyDictionary<TypeDefinitionHandle, FieldAndOffset[]> typeToFieldMap)
            {
                Module = module;
                GcStatics = gcStatics;
                NonGcStatics = nonGcStatics;
                ThreadGcStatics = threadGcStatics;
                ThreadNonGcStatics = threadNonGcStatics;
                TypeToFieldMap = typeToFieldMap;
            }
        }
    }
}