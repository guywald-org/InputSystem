using System;

////REVIEW: it probably makes sense to have an initial phase where we process the initial set of
////        device discoveries from native and keep the template cache around instead of throwing
////        it away after the creation of every single device; best approach may be to just
////        reuse the same InputControlSetup instance over and over

namespace ISX
{
    // Turns a template into a control hiearchy.
    // Ultimately produces a device but can also be used to query the control setup described
    // by a template.
    // Can be used to create setups as well as to adjust them later.
    // InputControlSetup is the *only* way to create control hierarchies.
    // Also computes a final state layout when setup is finished.
    // Once a setup has been established, it yields an independent control hierarchy and the setup itself
    // is abandoned.
    //
    // NOTE: InputControlSetups generate garbage. They are meant to be used for initialization only. Don't
    //       use them during normal gameplay.
    //
    // NOTE: Running an *existing* device through another control setup is a *destructive* operation.
    //       Existing controls may be reused while at the same time the hierarchy and even the device instance
    //       itself may change.
    public class InputControlSetup
    {
        public InputControlSetup(string template, InputDevice existingDevice = null, string variant = null)
        {
            Setup(template, existingDevice, variant);
        }

        internal void Setup(string template, InputDevice existingDevice, string variant)
        {
            if (existingDevice != null && existingDevice.m_DeviceIndex != InputDevice.kInvalidDeviceIndex)
                throw new InvalidOperationException(
                    $"Cannot modify control setup of existing device {existingDevice} while added to system.");

            if (string.IsNullOrEmpty(variant))
                variant = "default";
            else
                variant = variant.ToLower();

            AddControl(template, variant, null, null, existingDevice);
            FinalizeControlHierarchy();
            m_Device.CallFinishSetupRecursive(this);
        }

        ////TODO: do away with this
        // Complete the setup and return the full control hierarchy setup
        // with its device root.
        public InputDevice Finish()
        {
            var device = m_Device;

            // Kill off our state.
            Reset();

            return device;
        }

        // Look up a direct or indirect child control.
        public InputControl TryGetControl(InputControl parent, string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(nameof(path));

            if (m_Device == null)
                return null;

            if (parent == null)
                parent = m_Device;

            var childCount = parent.m_ChildrenReadOnly.Count;
            for (var i = 0; i < childCount; ++i)
            {
                var child = parent.m_ChildrenReadOnly[i];
                var match = PathHelpers.FindControl(child, path);
                if (match != null)
                    return match;
            }

            if (ReferenceEquals(parent, m_Device))
                return PathHelpers.FindControl(m_Device, $"{m_Device.name}/{path}");

            return null;
        }

        // Look up a direct or indirect chid control expected to be of a specific type.
        // Throws if actual type is not compatible.
        public TControl TryGetControl<TControl>(InputControl parent, string path)
            where TControl : InputControl
        {
            var control = TryGetControl(parent, path);
            if (control == null)
                return null;

            var controlOfType = control as TControl;
            if (controlOfType == null)
                throw new Exception($"Expected control '{path}' to be of type '{typeof(TControl).Name}' but is of type '{control.GetType().Name}' instead!");

            return controlOfType;
        }

        // Look up a direct or indirect child control.
        // Throws if control does not exist.
        public InputControl GetControl(InputControl parent, string path)
        {
            var control = TryGetControl(parent, path);
            if (control == null)
                throw new Exception($"Cannot find input control '{parent.MakeChildPath(path)}'");
            return control;
        }

        public TControl GetControl<TControl>(InputControl parent, string path)
            where TControl : InputControl
        {
            var control = GetControl(parent, path);

            var controlOfType = control as TControl;
            if (controlOfType == null)
                throw new Exception($"Expected control '{path}' to be of type '{typeof(TControl).Name}' but is of type '{control.GetType().Name}' instead!");

            return controlOfType;
        }

        public InputControl GetControl(string path)
        {
            var control = TryGetControl(path);
            if (control == null)
                throw new Exception($"Cannot find input control '{path}'");
            return control;
        }

        public InputControl TryGetControl(string path)
        {
            return TryGetControl(m_Device, path);
        }

        public TControl GetControl<TControl>(string path)
            where TControl : InputControl
        {
            var control = TryGetControl<TControl>(path);
            if (control == null)
                throw new Exception($"Cannot find input control '{path}'");
            return control;
        }

        public TControl TryGetControl<TControl>(string path)
            where TControl : InputControl
        {
            var control = TryGetControl(path);
            if (control == null)
                return null;

            var controlOfType = control as TControl;
            if (controlOfType == null)
                throw new Exception($"Expected control '{path}' to be of type '{typeof(TControl).Name}' but is of type '{control.GetType().Name}' instead!");

            return controlOfType;
        }

        private InputDevice m_Device;

        // We construct templates lazily as we go but keep them cached while we
        // set up hierarchies so that we don't re-construt the same Button template
        // 256 times for a keyboard.
        private InputTemplate.Cache m_TemplateCache;

        // Reset the setup in a way where it can be reused for another setup.
        // Should retain allocations that can be reused.
        private void Reset()
        {
            m_Device = null;
            // Leave the cache in place so we can reuse them in another setup path.
        }

        private InputControl AddControl(string template, string variant, string name, InputControl parent, InputControl existingControl)
        {
            // Look up template by name.
            var templateInstance = FindOrLoadTemplate(template);

            // Create control hiearchy.
            return AddControlRecursive(templateInstance, variant, name, parent, existingControl);
        }

        private InputControl AddControlRecursive(InputTemplate template, string variant, string name, InputControl parent, InputControl existingControl)
        {
            InputControl control;

            // If we have an existing control, see whether it's usable.
            if (existingControl != null && existingControl.template == template.name && existingControl.GetType() == template.type)
            {
                control = existingControl;
            }
            else
            {
                // No, so create a new control.
                var controlObject = Activator.CreateInstance(template.type);
                control = controlObject as InputControl;
                if (control == null)
                {
                    throw new Exception($"Type '{template.type.Name}' referenced by template '{template.name}' is not an InputControl");
                }
            }

            // If it's a device, perform some extra work specific to the control
            // hiearchy root.
            var controlAsDevice = control as InputDevice;
            if (controlAsDevice != null)
            {
                if (parent != null)
                    throw new Exception($"Cannot instantiate device template '{template.name}' as child of '{parent.path}'; devices must be added at root");

                m_Device = controlAsDevice;
                m_Device.m_StateBlock.byteOffset = 0;
                m_Device.m_StateBlock.format = template.format;

                // If we have an existing device, we'll start the various control arrays
                // from scratch. Note that all the controls still refer to the existing
                // arrays and so we can iterate children, for example, just fine while
                // we are rebuilding the control hierarchy.
                m_Device.m_AliasesForEachControl = null;
                m_Device.m_ChildrenForEachControl = null;
                m_Device.m_UsagesForEachControl = null;
                m_Device.m_UsageToControl = null;

                if (template.m_UpdateBeforeRender == true)
                    m_Device.m_Flags |= InputDevice.Flags.UpdateBeforeRender;
            }

            // Set common properties.
            if (name == null)
            {
                name = template.name;
            }

            control.m_Name = name;
            control.m_Template = template.name;
            control.m_Parent = parent;
            control.m_Device = m_Device;

            // Create children and configure their settings from our
            // template values.
            try
            {
                // Pass list of existing control on to function as we may have decided to not
                // actually reuse the existing control (and thus control.m_ChildrenReadOnly will
                // now be blank) but still want crawling down the hierarchy to preserve existing
                // controls where possible.
                AddChildControls(template, variant, control, existingControl?.m_ChildrenReadOnly);
            }
            catch
            {
                ////TODO: remove control from collection and rethrow
                throw;
            }


            // Finally come up with a layout for our state.
            ComputeStateLayout(control);

            return control;
        }

        private void AddChildControls(InputTemplate template, string variant, InputControl parent, ReadOnlyArray<InputControl>? existingChildren)
        {
            var controlTemplates = template.m_Controls;
            if (controlTemplates == null)
                return;

            // Find out how many direct children we will add.
            var childCount = 0;
            var haveControlTemplateWithPath = false;
            for (var i = 0; i < controlTemplates.Length; ++i)
            {
                // Not a new child if it's a template reaching in to the hierarchy to modify
                // an existing child.
                if (controlTemplates[i].isModifyingChildControlByPath)
                {
                    haveControlTemplateWithPath = true;
                    continue;
                }

                // Skip if variant doesn't match.
                if (!string.IsNullOrEmpty(controlTemplates[i].variant) &&
                    controlTemplates[i].variant.ToLower() != variant)
                    continue;

                ++childCount;
            }

            // Add room for us in the device's child array.
            var firstChildIndex = ArrayHelpers.GrowBy(ref m_Device.m_ChildrenForEachControl, childCount);

            // Add controls from all control templates except the ones that have
            // paths in them.
            var childIndex = firstChildIndex;
            for (var i = 0; i < controlTemplates.Length; ++i)
            {
                var controlTemplate = controlTemplates[i];

                // Skip control templates that don't add controls but rather modify child
                // controls of other controls added by the template. We do a seccond pass
                // to apply their settings.
                if (controlTemplate.isModifyingChildControlByPath)
                    continue;

                // If the control is part of a variant, skip it if it isn't the variant we're
                // looking for.
                if (!string.IsNullOrEmpty(controlTemplate.variant) && controlTemplate.variant.ToLower() != variant)
                    continue;

                ////REVIEW: can we check this in InputTemplate instead?
                if (string.IsNullOrEmpty(controlTemplate.template))
                    throw new Exception($"Template has not been set on control '{controlTemplate.name}' in '{template.name}'");

                // See if we have an existing control that we might be able to re-use.
                InputControl existingControl = null;
                if (existingChildren != null)
                {
                    var existingChildCount = existingChildren.Value.Count;
                    for (var n = 0; n < existingChildCount; ++n)
                    {
                        var existingChild = existingChildren.Value[n];
                        if (existingChild.template == controlTemplate.template
                            && existingChild.name.ToLower() == controlTemplate.name.ToLower())
                        {
                            existingControl = existingChild;
                            break;
                        }
                    }
                }

                // Create control.
                var control = AddControl(controlTemplate.template, variant, controlTemplate.name, parent, existingControl);

                // Add to array.
                m_Device.m_ChildrenForEachControl[childIndex] = control;
                ++childIndex;

                // Pass state block config on to control.
                control.m_StateBlock.byteOffset = controlTemplate.offset;
                if (controlTemplate.bit != InputStateBlock.kInvalidOffset)
                    control.m_StateBlock.bitOffset = controlTemplate.bit;
                if (controlTemplate.sizeInBits != 0)
                    control.m_StateBlock.sizeInBits = controlTemplate.sizeInBits;
                if (controlTemplate.format != 0)
                    SetFormat(control, controlTemplate);

                ////REVIEW: the constant appending to m_UsagesForEachControl and m_AliasesForEachControl may lead to a lot
                ////        of successive re-allocations

                // Add usages.
                if (controlTemplate.usages.Count > 0)
                {
                    var usageCount = controlTemplate.usages.Count;
                    var usageIndex = ArrayHelpers.AppendToImmutable(ref m_Device.m_UsagesForEachControl, controlTemplate.usages.m_Array);
                    control.m_UsagesReadOnly = new ReadOnlyArray<string>(m_Device.m_UsagesForEachControl, usageIndex, usageCount);

                    ArrayHelpers.GrowBy(ref m_Device.m_UsageToControl, usageCount);
                    for (var n = 0; n < usageCount; ++n)
                        m_Device.m_UsageToControl[usageIndex + n] = control;
                }

                // Add aliases.
                if (controlTemplate.aliases.Count > 0)
                {
                    var aliasCount = controlTemplate.aliases.Count;
                    var aliasIndex = ArrayHelpers.AppendToImmutable(ref m_Device.m_AliasesForEachControl, controlTemplate.aliases.m_Array);
                    control.m_AliasesReadOnly = new ReadOnlyArray<string>(m_Device.m_AliasesForEachControl, aliasIndex, aliasCount);
                }

                // Set parameters.
                if (controlTemplate.parameters.Count > 0)
                    SetParameters(control, controlTemplate.parameters);

                // Add processors.
                if (controlTemplate.processors.Count > 0)
                    AddProcessors(control, controlTemplate, template.name);
            }

            // Install child array on parent. We will later patch up the array
            // reference again as we finalize the hierarchy. However, the reference
            // will point to a valid child array all the same even while we are
            // constructing the hiearchy.
            //
            // NOTE: It's important to do this *after* the loop above where we call AddControl for each child
            //       as each child may end up moving the m_ChildrenForEachControl array around.
            parent.m_ChildrenReadOnly = new ReadOnlyArray<InputControl>(m_Device.m_ChildrenForEachControl, firstChildIndex, childCount);

            // Apply control modifications from control templates with paths.
            if (haveControlTemplateWithPath)
            {
                for (var i = 0; i < controlTemplates.Length; ++i)
                {
                    var controlTemplate = controlTemplates[i];
                    if (!controlTemplate.isModifyingChildControlByPath)
                        continue;

                    // Find the child control.
                    var child = TryGetControl(parent, controlTemplate.name);
                    if (child == null)
                        throw new Exception(
                            $"Cannot find control '{controlTemplate.name}' in template '{template.name}'");

                    // Controls layout themselves as we come back up the hierarchy. However, when we
                    // apply layout modifications reaching *into* the hierarchy, we need to retrigger
                    // layouting on their parents.
                    var haveChangedLayoutOfChild = false;

                    // Apply modifications.
                    if (controlTemplate.sizeInBits != 0 && child.m_StateBlock.sizeInBits != controlTemplate.sizeInBits)
                    {
                        child.m_StateBlock.sizeInBits = controlTemplate.sizeInBits;
                    }
                    if (controlTemplate.format != 0 && child.m_StateBlock.format != controlTemplate.format)
                    {
                        SetFormat(child, controlTemplate);
                        haveChangedLayoutOfChild = true;
                    }
                    ////REVIEW: ATM, when you move a child with a fixed offset, we only move the child
                    ////        and don't move the parent or siblings. What this means is that if you move
                    ////        leftStick/x, for example, leftStick stays put. ATM you have to move *all*
                    ////        controls that are part of a chain manually. Not sure what the best behavior
                    ////        is. If we opt to move parents along with children, we have to make sure we
                    ////        are not colliding with any other relocations of children (e.g. if you move
                    ////        both leftStick/x and leftStick/y, leftStick itself should move only once and
                    ////        not at all if there indeed is a leftStick control template with an offset;
                    ////        so, it'd get quite complicated)
                    if (controlTemplate.offset != InputStateBlock.kInvalidOffset)
                        child.m_StateBlock.byteOffset = controlTemplate.offset;
                    if (controlTemplate.bit != InputStateBlock.kInvalidOffset)
                        child.m_StateBlock.bitOffset = controlTemplate.bit;
                    if (controlTemplate.processors.Count > 0)
                        AddProcessors(child, controlTemplate, template.name);
                    if (controlTemplate.parameters.Count > 0)
                        SetParameters(child, controlTemplate.parameters);

                    ////TODO: other modifications

                    // Apply layout change.
                    ////REVIEW: not sure what's better here; trigger this immediately means we may trigger
                    ////        it a number of times on the same parent but doing it as a final pass would
                    ////        require either collecting the necessary parents or doing another pass through
                    ////        the list of control templates
                    if (haveChangedLayoutOfChild && !ReferenceEquals(child.parent, parent))
                        ComputeStateLayout(child.parent);
                }
            }
        }

        private static void AddProcessors(InputControl control, InputTemplate.ControlTemplate controlTemplate, string templateName)
        {
            var processorCount = controlTemplate.processors.Count;
            for (var n = 0; n < processorCount; ++n)
            {
                var name = controlTemplate.processors[n].name;
                var type = InputProcessor.TryGet(name);
                if (type == null)
                    throw new Exception(
                        $"Cannot find processor '{name}' referenced by control '{controlTemplate.name}' in template '{templateName}'");

                var processor = Activator.CreateInstance(type);

                var parameters = controlTemplate.processors[n].parameters;
                if (parameters.Count > 0)
                    SetParameters(processor, parameters);

                control.AddProcessor(processor);
            }
        }

        internal static void SetParameters(object onObject, ReadOnlyArray<InputTemplate.ParameterValue> parameters)
        {
            var objectType = onObject.GetType();
            for (var i = 0; i < parameters.Count; ++i)
            {
                var parameter = parameters[i];

                var field = objectType.GetField(parameter.name);
                if (field == null)
                    throw new Exception($"Cannot find public field {parameter.name} in {objectType.Name} (referenced by parameter)");

                ////REVIEW: can we do this without boxing?

                object value = null;
                unsafe
                {
                    switch (parameter.type)
                    {
                        case InputTemplate.ParameterType.Boolean:
                            value = *((bool*)parameter.value);
                            break;
                        case InputTemplate.ParameterType.Integer:
                            value = *((int*)parameter.value);
                            break;
                        case InputTemplate.ParameterType.Float:
                            value = *((float*)parameter.value);
                            break;
                    }
                }

                field.SetValue(onObject, value);
            }
        }

        private static void SetFormat(InputControl control, InputTemplate.ControlTemplate controlTemplate)
        {
            control.m_StateBlock.format = controlTemplate.format;
            if (controlTemplate.sizeInBits == 0)
            {
                var primitiveFormatSize = InputStateBlock.GetSizeOfPrimitiveFormatInBits(controlTemplate.format);
                if (primitiveFormatSize != -1)
                    control.m_StateBlock.sizeInBits = (uint)primitiveFormatSize;
            }
        }

        private InputTemplate FindOrLoadTemplate(string name)
        {
            return m_TemplateCache.FindOrLoadTemplate(name);
        }

        private void ComputeStateLayout(InputControl control)
        {
            var children = control.m_ChildrenReadOnly;

            // If the control has a format but no size specified and the format is a
            // primitive format, just set the size automatically.
            if (control.m_StateBlock.sizeInBits == 0 && control.m_StateBlock.format != 0)
            {
                var sizeInBits = InputStateBlock.GetSizeOfPrimitiveFormatInBits(control.m_StateBlock.format);
                if (sizeInBits != -1)
                    control.m_StateBlock.sizeInBits = (uint)sizeInBits;
            }

            // If state size is not set, it means it's computed from the size of the
            // children so make sure we actually have children.
            if (control.m_StateBlock.sizeInBits == 0 && children.Count == 0)
            {
                throw new Exception(
                    $"Control '{control.path}' with template '{control.template}' has no size set but has no children to compute size from");
            }

            // If there's no children, our job is done.
            if (children.Count == 0)
                return;

            // First deal with children that want fixed offsets. All the other ones
            // will get appended to the end.
            var firstUnfixedByteOffset = 0u;
            foreach (var child in children)
            {
                // Skip children that don't have fixed offsets.
                if (child.m_StateBlock.byteOffset == InputStateBlock.kInvalidOffset)
                    continue;

                if (child.m_StateBlock.byteOffset >= firstUnfixedByteOffset)
                    firstUnfixedByteOffset =
                        BitfieldHelpers.ComputeFollowingByteOffset(child.m_StateBlock.byteOffset, child.m_StateBlock.sizeInBits);
            }

            ////TODO: this doesn't support mixed automatic and fixed layouting *within* bitfields;
            ////      I think it's okay not to support that but we should at least detect it

            // Now assign an offset to every control that wants an
            // automatic offset. For bitfields, we need to delay advancing byte
            // offsets until we've seen all bits in the fields.
            // NOTE: Bit addressing controls using automatic offsets *must* be consecutive.
            var runningByteOffset = firstUnfixedByteOffset;
            InputControl firstBitAddressingChild = null;
            var bitfieldSizeInBits = 0u;
            foreach (var child in children)
            {
                // Skip children with fixed offsets.
                if (child.m_StateBlock.byteOffset != InputStateBlock.kInvalidOffset)
                    continue;

                // See if it's a bit addressing control.
                var isBitAddressingChild = (child.m_StateBlock.sizeInBits % 8) != 0;
                if (isBitAddressingChild)
                {
                    // Remember start of bitfield group.
                    if (firstBitAddressingChild == null)
                        firstBitAddressingChild = child;

                    // Keep a running count of the size of the bitfield.
                    if (child.m_StateBlock.bitOffset == InputStateBlock.kInvalidOffset)
                        bitfieldSizeInBits += child.m_StateBlock.sizeInBits;
                    else
                    {
                        var lastBit = child.m_StateBlock.bitOffset + child.m_StateBlock.sizeInBits;
                        if (lastBit > bitfieldSizeInBits)
                            bitfieldSizeInBits = lastBit;
                    }
                }
                else
                {
                    // Terminate bitfield group (if there was one).
                    if (firstBitAddressingChild != null)
                    {
                        runningByteOffset = BitfieldHelpers.ComputeFollowingByteOffset(runningByteOffset, bitfieldSizeInBits);
                        firstBitAddressingChild = null;
                    }
                }

                child.m_StateBlock.byteOffset = runningByteOffset;

                if (!isBitAddressingChild)
                    runningByteOffset =
                        BitfieldHelpers.ComputeFollowingByteOffset(runningByteOffset, child.m_StateBlock.sizeInBits);
            }

            // Compute total size.
            // If we ended on a bitfield, account for its size.
            if (firstBitAddressingChild != null)
                runningByteOffset = BitfieldHelpers.ComputeFollowingByteOffset(runningByteOffset, bitfieldSizeInBits);
            var totalSizeInBytes = runningByteOffset;

            // Set size. We force all parents to the combined size of their children.
            control.m_StateBlock.sizeInBits = totalSizeInBytes * 8;
        }

        // Finalize array references in the control hierarchy and make all state offets relative to the
        // device root.
        private void FinalizeControlHierarchy()
        {
            // Running indices.
            var childArrayIndex = 0;
            var usageArrayIndex = 0;
            var aliasArrayIndex = 0;

            FinalizeControlHierarchyRecursive(m_Device, ref childArrayIndex, ref usageArrayIndex,
                ref aliasArrayIndex);
        }

        private void FinalizeControlHierarchyRecursive(InputControl control, ref int childArrayIndex,
            ref int usageArrayIndex, ref int aliasArrayIndex)
        {
            // Finalize child, usage, and alias array references.
            // When we get here, all the array references are valid but we may have grown the arrays on
            // m_Device repeatedly so we want all controls to refer to those final arrays now so that the
            // garbage collector can reclaim the intermediate arrays.
            FinalizeReadonlyArray(ref control.m_ChildrenReadOnly, m_Device.m_ChildrenForEachControl, ref childArrayIndex);
            FinalizeReadonlyArray(ref control.m_UsagesReadOnly, m_Device.m_UsagesForEachControl, ref usageArrayIndex);
            FinalizeReadonlyArray(ref control.m_AliasesReadOnly, m_Device.m_AliasesForEachControl, ref aliasArrayIndex);

            // Recurse into children. Also bake our state offset into our children.
            var ourOffset = control.m_StateBlock.byteOffset;
            foreach (var child in control.m_ChildrenReadOnly)
            {
                child.m_StateBlock.byteOffset += ourOffset;
                FinalizeControlHierarchyRecursive(child, ref childArrayIndex, ref usageArrayIndex,
                    ref aliasArrayIndex);
            }
        }

        private static void FinalizeReadonlyArray<TValue>(ref ReadOnlyArray<TValue> array, TValue[] masterArray,
            ref int runningIndex)
        {
            var elementCount = array.Count;
            if (elementCount == 0)
                return;

            array = new ReadOnlyArray<TValue>(masterArray, runningIndex, elementCount);
            runningIndex += elementCount;
        }
    }
}
