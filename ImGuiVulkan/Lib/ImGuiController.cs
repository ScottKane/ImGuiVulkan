﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Input.Extensions;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Silk.NET.Vulkan.Extensions.ImGui;

public class ImGuiController : IDisposable
{
    private readonly List<char> _pressedChars = new();
    private ulong _bufferMemoryAlignment = 256;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private DescriptorSetLayout _descriptorSetLayout;
    private Device _device;
    private Image _fontImage;
    private DeviceMemory _fontMemory;
    private Sampler _fontSampler;
    private ImageView _fontView;
    private bool _frameBegun;
    private GlobalMemory _frameRenderBuffers;
    private IInputContext _input;
    private IKeyboard _keyboard;
    private WindowRenderBuffers _mainWindowRenderBuffers;
    private PhysicalDevice _physicalDevice;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private RenderPass _renderPass;
    private ShaderModule _shaderModuleFrag;
    private ShaderModule _shaderModuleVert;
    private int _swapChainImageCt;
    private IView _view;
    private Vk _vk;
    private int _windowHeight;
    private int _windowWidth;

    /// <summary>
    ///     Constructs a new ImGuiController.
    /// </summary>
    /// <param name="vk">The vulkan api instance</param>
    /// <param name="view">Window view</param>
    /// <param name="input">Input context</param>
    /// <param name="physicalDevice">The physical device instance in use</param>
    /// <param name="graphicsFamilyIndex">The graphics family index corresponding to the graphics queue</param>
    /// <param name="swapChainImageCt">The number of images used in the swap chain</param>
    /// <param name="swapChainFormat">The image format used by the swap chain</param>
    /// <param name="depthBufferFormat">The image formate used by the depth buffer, or null if no depth buffer is used</param>
    public ImGuiController(Vk vk, IView view, IInputContext input, PhysicalDevice physicalDevice,
        uint graphicsFamilyIndex, int swapChainImageCt, Format swapChainFormat, Format? depthBufferFormat)
    {
        var context = ImGuiNET.ImGui.CreateContext();
        ImGuiNET.ImGui.SetCurrentContext(context);

        // Use the default font
        var io = ImGuiNET.ImGui.GetIO();
        io.Fonts.AddFontDefault();
        // io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        //
        // io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;
        // io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        Init(vk, view, input, physicalDevice, graphicsFamilyIndex, swapChainImageCt, swapChainFormat,
            depthBufferFormat);

        SetKeyMappings();

        SetPerFrameImGuiData(1f / 60f);

        BeginFrame();
    }

    /// <summary>
    ///     Constructs a new ImGuiController.
    /// </summary>
    /// <param name="vk">The vulkan api instance</param>
    /// <param name="view">Window view</param>
    /// <param name="input">Input context</param>
    /// <param name="imGuiFontConfig">A custom ImGui configuration</param>
    /// <param name="physicalDevice">The physical device instance in use</param>
    /// <param name="graphicsFamilyIndex">The graphics family index corresponding to the graphics queue</param>
    /// <param name="swapChainImageCt">The number of images used in the swap chain</param>
    /// <param name="swapChainFormat">The image format used by the swap chain</param>
    /// <param name="depthBufferFormat">The image formate used by the depth buffer, or null if no depth buffer is used</param>
    public unsafe ImGuiController(Vk vk, IView view, IInputContext input, ImGuiFontConfig imGuiFontConfig,
        PhysicalDevice physicalDevice, uint graphicsFamilyIndex, int swapChainImageCt, Format swapChainFormat,
        Format? depthBufferFormat)
    {
        var context = ImGuiNET.ImGui.CreateContext();
        ImGuiNET.ImGui.SetCurrentContext(context);

        // Upload custom ImGui font
        var io = ImGuiNET.ImGui.GetIO();
        // io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        //
        // io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;
        // io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        if (io.Fonts.AddFontFromFileTTF(imGuiFontConfig.FontPath, imGuiFontConfig.FontSize).NativePtr == default)
            throw new Exception("Failed to load ImGui font");

        Init(vk, view, input, physicalDevice, graphicsFamilyIndex, swapChainImageCt, swapChainFormat,
            depthBufferFormat);

        SetKeyMappings();

        SetPerFrameImGuiData(1f / 60f);

        BeginFrame();
    }

    /// <summary>
    ///     Frees all graphics resources used by the renderer.
    /// </summary>
    public unsafe void Dispose()
    {
        _view.Resize -= WindowResized;
        _keyboard.KeyChar -= OnKeyChar;

        for (uint n = 0; n < _mainWindowRenderBuffers.Count; n++)
        {
            _vk.DestroyBuffer(_device, _mainWindowRenderBuffers.FrameRenderBuffers[n].VertexBuffer, default);
            _vk.FreeMemory(_device, _mainWindowRenderBuffers.FrameRenderBuffers[n].VertexBufferMemory, default);
            _vk.DestroyBuffer(_device, _mainWindowRenderBuffers.FrameRenderBuffers[n].IndexBuffer, default);
            _vk.FreeMemory(_device, _mainWindowRenderBuffers.FrameRenderBuffers[n].IndexBufferMemory, default);
        }

        _vk.DestroyShaderModule(_device, _shaderModuleVert, default);
        _vk.DestroyShaderModule(_device, _shaderModuleFrag, default);
        _vk.DestroyImageView(_device, _fontView, default);
        _vk.DestroyImage(_device, _fontImage, default);
        _vk.FreeMemory(_device, _fontMemory, default);
        _vk.DestroySampler(_device, _fontSampler, default);
        _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, default);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, default);
        _vk.DestroyPipeline(_device, _pipeline, default);
        _vk.DestroyDescriptorPool(_vk.CurrentDevice.Value, _descriptorPool, default);
        _vk.DestroyRenderPass(_vk.CurrentDevice.Value, _renderPass, default);

        ImGuiNET.ImGui.DestroyContext();
    }

    private unsafe void Init(Vk vk, IView view, IInputContext input, PhysicalDevice physicalDevice,
        uint graphicsFamilyIndex, int swapChainImageCt, Format swapChainFormat, Format? depthBufferFormat)
    {
        _vk = vk;
        _view = view;
        _input = input;
        _physicalDevice = physicalDevice;
        _windowWidth = view.Size.X;
        _windowHeight = view.Size.Y;
        _swapChainImageCt = swapChainImageCt;

        if (swapChainImageCt < 2) throw new Exception("Swap chain image count must be >= 2");

        if (!_vk.CurrentDevice.HasValue)
            throw new InvalidOperationException(
                "vk.CurrentDevice is null. _vk.CurrentDevice must be set to the current device.");

        _device = _vk.CurrentDevice.Value;

        // Set default style
        ImGuiNET.ImGui.StyleColorsDark();

        // Create the descriptor pool for ImGui
        Span<DescriptorPoolSize> poolSizes = stackalloc DescriptorPoolSize[]
            {new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1)};
        var descriptorPool = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint) poolSizes.Length,
            PPoolSizes = (DescriptorPoolSize*) Unsafe.AsPointer(ref poolSizes.GetPinnableReference()),
            MaxSets = 1
        };
        if (_vk.CreateDescriptorPool(_device, descriptorPool, default, out _descriptorPool) != Result.Success)
            throw new Exception("Unable to create descriptor pool");

        // Create the render pass
        var colorAttachment = new AttachmentDescription
        {
            Format = swapChainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = AttachmentLoadOp.Load == AttachmentLoadOp.Clear
                ? ImageLayout.Undefined
                : ImageLayout.PresentSrcKhr,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = (AttachmentReference*) Unsafe.AsPointer(ref colorAttachmentRef)
        };

        Span<AttachmentDescription> attachments = stackalloc AttachmentDescription[] {colorAttachment};
        var depthAttachment = new AttachmentDescription();
        var depthAttachmentRef = new AttachmentReference();
        if (depthBufferFormat.HasValue)
        {
            depthAttachment.Format = depthBufferFormat.Value;
            depthAttachment.Samples = SampleCountFlags.Count1Bit;
            depthAttachment.LoadOp = AttachmentLoadOp.Load;
            depthAttachment.StoreOp = AttachmentStoreOp.DontCare;
            depthAttachment.StencilLoadOp = AttachmentLoadOp.DontCare;
            depthAttachment.StencilStoreOp = AttachmentStoreOp.DontCare;
            depthAttachment.InitialLayout = AttachmentLoadOp.Load == AttachmentLoadOp.Clear
                ? ImageLayout.Undefined
                : ImageLayout.DepthStencilAttachmentOptimal;
            depthAttachment.FinalLayout = ImageLayout.DepthStencilAttachmentOptimal;

            depthAttachmentRef.Attachment = 1;
            depthAttachmentRef.Layout = ImageLayout.DepthStencilAttachmentOptimal;

            subpass.PDepthStencilAttachment = (AttachmentReference*) Unsafe.AsPointer(ref depthAttachmentRef);

            attachments = stackalloc AttachmentDescription[] {colorAttachment, depthAttachment};
        }

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = (uint) attachments.Length,
            PAttachments = (AttachmentDescription*) Unsafe.AsPointer(ref attachments.GetPinnableReference()),
            SubpassCount = 1,
            PSubpasses = (SubpassDescription*) Unsafe.AsPointer(ref subpass),
            DependencyCount = 1,
            PDependencies = (SubpassDependency*) Unsafe.AsPointer(ref dependency)
        };

        if (_vk.CreateRenderPass(_device, renderPassInfo, default, out _renderPass) != Result.Success)
            throw new Exception("Failed to create render pass");

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MinLod = -1000,
            MaxLod = 1000,
            MaxAnisotropy = 1.0f
        };
        if (vk.CreateSampler(_device, info, default, out _fontSampler) != Result.Success)
            throw new Exception("Unable to create sampler");

        var sampler = _fontSampler;

        var binding = new DescriptorSetLayoutBinding
        {
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = (Sampler*) Unsafe.AsPointer(ref sampler)
        };

        var descriptorInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = (DescriptorSetLayoutBinding*) Unsafe.AsPointer(ref binding)
        };
        if (vk.CreateDescriptorSetLayout(_device, descriptorInfo, default, out _descriptorSetLayout) != Result.Success)
            throw new Exception("Unable to create descriptor set layout");

        fixed (DescriptorSetLayout* pg_DescriptorSetLayout = &_descriptorSetLayout)
        {
            var alloc_info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = pg_DescriptorSetLayout
            };
            if (vk.AllocateDescriptorSets(_device, alloc_info, out _descriptorSet) != Result.Success)
                throw new Exception("Unable to create descriptor sets");
        }

        var vertPushConst = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = sizeof(float) * 0,
            Size = sizeof(float) * 4
        };

        var set_layout = _descriptorSetLayout;
        var layout_info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = (DescriptorSetLayout*) Unsafe.AsPointer(ref set_layout),
            PushConstantRangeCount = 1,
            PPushConstantRanges = (PushConstantRange*) Unsafe.AsPointer(ref vertPushConst)
        };
        if (vk.CreatePipelineLayout(_device, layout_info, default, out _pipelineLayout) != Result.Success)
            throw new Exception("Unable to create the descriptor set layout");

        // Create the shader modules
        if (_shaderModuleVert.Handle == default)
            fixed (uint* vertShaderBytes = &Shaders.VertexShader[0])
            {
                var vert_info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint) Shaders.VertexShader.Length * sizeof(uint),
                    PCode = vertShaderBytes
                };
                if (vk.CreateShaderModule(_device, vert_info, default, out _shaderModuleVert) != Result.Success)
                    throw new Exception("Unable to create the vertex shader");
            }

        if (_shaderModuleFrag.Handle == default)
            fixed (uint* fragShaderBytes = &Shaders.FragmentShader[0])
            {
                var frag_info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint) Shaders.FragmentShader.Length * sizeof(uint),
                    PCode = fragShaderBytes
                };
                if (vk.CreateShaderModule(_device, frag_info, default, out _shaderModuleFrag) != Result.Success)
                    throw new Exception("Unable to create the fragment shader");
            }

        // Create the pipeline
        Span<PipelineShaderStageCreateInfo> stage = stackalloc PipelineShaderStageCreateInfo[2];
        stage[0].SType = StructureType.PipelineShaderStageCreateInfo;
        stage[0].Stage = ShaderStageFlags.VertexBit;
        stage[0].Module = _shaderModuleVert;
        stage[0].PName = (byte*) SilkMarshal.StringToPtr("main");
        stage[1].SType = StructureType.PipelineShaderStageCreateInfo;
        stage[1].Stage = ShaderStageFlags.FragmentBit;
        stage[1].Module = _shaderModuleFrag;
        stage[1].PName = (byte*) SilkMarshal.StringToPtr("main");

        var binding_desc = new VertexInputBindingDescription
        {
            Stride = (uint) Unsafe.SizeOf<ImDrawVert>(),
            InputRate = VertexInputRate.Vertex
        };

        Span<VertexInputAttributeDescription> attribute_desc = stackalloc VertexInputAttributeDescription[3];
        attribute_desc[0].Location = 0;
        attribute_desc[0].Binding = binding_desc.Binding;
        attribute_desc[0].Format = Format.R32G32Sfloat;
        attribute_desc[0].Offset = (uint) Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos));
        attribute_desc[1].Location = 1;
        attribute_desc[1].Binding = binding_desc.Binding;
        attribute_desc[1].Format = Format.R32G32Sfloat;
        attribute_desc[1].Offset = (uint) Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv));
        attribute_desc[2].Location = 2;
        attribute_desc[2].Binding = binding_desc.Binding;
        attribute_desc[2].Format = Format.R8G8B8A8Unorm;
        attribute_desc[2].Offset = (uint) Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col));

        var vertex_info = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = (VertexInputBindingDescription*) Unsafe.AsPointer(ref binding_desc),
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = (VertexInputAttributeDescription*) Unsafe.AsPointer(ref attribute_desc[0])
        };

        var ia_info = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };

        var viewport_info = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1
        };

        var raster_info = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1.0f
        };

        var ms_info = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };

        var color_attachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = new Bool32(true),
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };

        var depth_info = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo
        };

        var blend_info = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = (PipelineColorBlendAttachmentState*) Unsafe.AsPointer(ref color_attachment)
        };

        Span<DynamicState> dynamic_states = stackalloc DynamicState[] {DynamicState.Viewport, DynamicState.Scissor};
        var dynamic_state = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint) dynamic_states.Length,
            PDynamicStates = (DynamicState*) Unsafe.AsPointer(ref dynamic_states[0])
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            Flags = default,
            StageCount = 2,
            PStages = (PipelineShaderStageCreateInfo*) Unsafe.AsPointer(ref stage[0]),
            PVertexInputState = (PipelineVertexInputStateCreateInfo*) Unsafe.AsPointer(ref vertex_info),
            PInputAssemblyState = (PipelineInputAssemblyStateCreateInfo*) Unsafe.AsPointer(ref ia_info),
            PViewportState = (PipelineViewportStateCreateInfo*) Unsafe.AsPointer(ref viewport_info),
            PRasterizationState = (PipelineRasterizationStateCreateInfo*) Unsafe.AsPointer(ref raster_info),
            PMultisampleState = (PipelineMultisampleStateCreateInfo*) Unsafe.AsPointer(ref ms_info),
            PDepthStencilState = (PipelineDepthStencilStateCreateInfo*) Unsafe.AsPointer(ref depth_info),
            PColorBlendState = (PipelineColorBlendStateCreateInfo*) Unsafe.AsPointer(ref blend_info),
            PDynamicState = (PipelineDynamicStateCreateInfo*) Unsafe.AsPointer(ref dynamic_state),
            Layout = _pipelineLayout,
            RenderPass = _renderPass,
            Subpass = 0
        };
        if (vk.CreateGraphicsPipelines(_device, default, 1, pipelineInfo, default, out _pipeline) != Result.Success)
            throw new Exception("Unable to create the pipeline");

        SilkMarshal.Free((nint) stage[0].PName);
        SilkMarshal.Free((nint) stage[1].PName);

        // Initialise ImGui Vulkan adapter
        var io = ImGuiNET.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height);
        var upload_size = (ulong) (width * height * 4 * sizeof(byte));

        // Submit one-time command to create the fonts texture
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = graphicsFamilyIndex
        };
        if (_vk.CreateCommandPool(_device, poolInfo, null, out var commandPool) != Result.Success)
            throw new Exception("failed to create command pool!");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        if (_vk.AllocateCommandBuffers(_device, allocInfo, out var commandBuffer) != Result.Success)
            throw new Exception("Unable to allocate command buffers");

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        if (_vk.BeginCommandBuffer(commandBuffer, beginInfo) != Result.Success)
            throw new Exception("Failed to begin a command buffer");

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm
        };
        imageInfo.Extent.Width = (uint) width;
        imageInfo.Extent.Height = (uint) height;
        imageInfo.Extent.Depth = 1;
        imageInfo.MipLevels = 1;
        imageInfo.ArrayLayers = 1;
        imageInfo.Samples = SampleCountFlags.Count1Bit;
        imageInfo.Tiling = ImageTiling.Optimal;
        imageInfo.Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
        imageInfo.SharingMode = SharingMode.Exclusive;
        imageInfo.InitialLayout = ImageLayout.Undefined;
        if (_vk.CreateImage(_device, imageInfo, default, out _fontImage) != Result.Success)
            throw new Exception("Failed to create font image");
        _vk.GetImageMemoryRequirements(_device, _fontImage, out var fontReq);
        var fontAllocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = fontReq.Size,
            MemoryTypeIndex = GetMemoryTypeIndex(vk, MemoryPropertyFlags.DeviceLocalBit, fontReq.MemoryTypeBits)
        };
        if (_vk.AllocateMemory(_device, &fontAllocInfo, default, out _fontMemory) != Result.Success)
            throw new Exception("Failed to allocate device memory");
        if (_vk.BindImageMemory(_device, _fontImage, _fontMemory, 0) != Result.Success)
            throw new Exception("Failed to bind device memory");

        var imageViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _fontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm
        };
        imageViewInfo.SubresourceRange.AspectMask = ImageAspectFlags.ColorBit;
        imageViewInfo.SubresourceRange.LevelCount = 1;
        imageViewInfo.SubresourceRange.LayerCount = 1;
        if (_vk.CreateImageView(_device, &imageViewInfo, default, out _fontView) != Result.Success)
            throw new Exception("Failed to create an image view");

        var descImageInfo = new DescriptorImageInfo
        {
            Sampler = _fontSampler,
            ImageView = _fontView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var writeDescriptors = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = (DescriptorImageInfo*) Unsafe.AsPointer(ref descImageInfo)
        };
        _vk.UpdateDescriptorSets(_device, 1, writeDescriptors, 0, default);

        // Create the Upload Buffer:
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = upload_size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateBuffer(_device, bufferInfo, default, out var uploadBuffer) != Result.Success)
            throw new Exception("Failed to create a device buffer");

        _vk.GetBufferMemoryRequirements(_device, uploadBuffer, out var uploadReq);
        _bufferMemoryAlignment =
            _bufferMemoryAlignment > uploadReq.Alignment ? _bufferMemoryAlignment : uploadReq.Alignment;

        var uploadAllocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = uploadReq.Size,
            MemoryTypeIndex = GetMemoryTypeIndex(vk, MemoryPropertyFlags.HostVisibleBit, uploadReq.MemoryTypeBits)
        };
        if (_vk.AllocateMemory(_device, uploadAllocInfo, default, out var uploadBufferMemory) != Result.Success)
            throw new Exception("Failed to allocate device memory");
        if (_vk.BindBufferMemory(_device, uploadBuffer, uploadBufferMemory, 0) != Result.Success)
            throw new Exception("Failed to bind device memory");

        void* map = null;
        if (_vk.MapMemory(_device, uploadBufferMemory, 0, upload_size, 0, &map) != Result.Success)
            throw new Exception("Failed to map device memory");
        Unsafe.CopyBlock(map, pixels.ToPointer(), (uint) upload_size);

        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = uploadBufferMemory,
            Size = upload_size
        };
        if (_vk.FlushMappedMemoryRanges(_device, 1, range) != Result.Success)
            throw new Exception("Failed to flush memory to device");
        _vk.UnmapMemory(_device, uploadBufferMemory);

        const uint VK_QUEUE_FAMILY_IGNORED = ~0U;

        var copyBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            Image = _fontImage
        };
        copyBarrier.SubresourceRange.AspectMask = ImageAspectFlags.ColorBit;
        copyBarrier.SubresourceRange.LevelCount = 1;
        copyBarrier.SubresourceRange.LayerCount = 1;
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.HostBit, PipelineStageFlags.TransferBit, 0, 0, default,
            0, default, 1, copyBarrier);

        var region = new BufferImageCopy();
        region.ImageSubresource.AspectMask = ImageAspectFlags.ColorBit;
        region.ImageSubresource.LayerCount = 1;
        region.ImageExtent.Width = (uint) width;
        region.ImageExtent.Height = (uint) height;
        region.ImageExtent.Depth = 1;
        _vk.CmdCopyBufferToImage(commandBuffer, uploadBuffer, _fontImage, ImageLayout.TransferDstOptimal, 1, &region);

        var use_barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            Image = _fontImage
        };
        use_barrier.SubresourceRange.AspectMask = ImageAspectFlags.ColorBit;
        use_barrier.SubresourceRange.LevelCount = 1;
        use_barrier.SubresourceRange.LayerCount = 1;
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0,
            0, default, 0, default, 1, use_barrier);

        // Store our identifier
        io.Fonts.SetTexID((IntPtr) _fontImage.Handle);

        if (_vk.EndCommandBuffer(commandBuffer) != Result.Success)
            throw new Exception("Failed to begin a command buffer");

        _vk.GetDeviceQueue(_device, graphicsFamilyIndex, 0, out var graphicsQueue);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = (CommandBuffer*) Unsafe.AsPointer(ref commandBuffer)
        };
        if (_vk.QueueSubmit(graphicsQueue, 1, submitInfo, default) != Result.Success)
            throw new Exception("Failed to begin a command buffer");

        if (_vk.QueueWaitIdle(graphicsQueue) != Result.Success) throw new Exception("Failed to begin a command buffer");

        _vk.DestroyBuffer(_device, uploadBuffer, default);
        _vk.FreeMemory(_device, uploadBufferMemory, default);
        _vk.DestroyCommandPool(_device, commandPool, default);
    }

    private uint GetMemoryTypeIndex(Vk vk, MemoryPropertyFlags properties, uint type_bits)
    {
        vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var prop);
        for (var i = 0; i < prop.MemoryTypeCount; i++)
            if ((prop.MemoryTypes[i].PropertyFlags & properties) == properties && (type_bits & (1u << i)) != 0)
                return (uint) i;
        return 0xFFFFFFFF; // Unable to find memoryType
    }

    private void BeginFrame()
    {
        ImGuiNET.ImGui.NewFrame();
        _frameBegun = true;
        _keyboard = _input.Keyboards[0];
        _view.Resize += WindowResized;
        _keyboard.KeyChar += OnKeyChar;
    }

    private void OnKeyChar(IKeyboard arg1, char arg2)
    {
        _pressedChars.Add(arg2);
    }

    private void WindowResized(Vector2D<int> size)
    {
        _windowWidth = size.X;
        _windowHeight = size.Y;
    }

    /// <summary>
    ///     Renders the ImGui draw list data.
    /// </summary>
    public void Render(CommandBuffer commandBuffer, Framebuffer framebuffer, Extent2D swapChainExtent)
    {
        if (_frameBegun)
        {
            _frameBegun = false;
            ImGuiNET.ImGui.Render();
            RenderImDrawData(ImGuiNET.ImGui.GetDrawData(), commandBuffer, framebuffer, swapChainExtent);
        }
    }

    /// <summary>
    ///     Updates ImGui input and IO configuration state. Call Update() before drawing and rendering.
    /// </summary>
    public void Update(float deltaSeconds)
    {
        if (_frameBegun) ImGuiNET.ImGui.Render();

        SetPerFrameImGuiData(deltaSeconds);
        UpdateImGuiInput();

        _frameBegun = true;
        ImGuiNET.ImGui.NewFrame();
    }

    private void SetPerFrameImGuiData(float deltaSeconds)
    {
        var io = ImGuiNET.ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);

        if (_windowWidth > 0 && _windowHeight > 0)
            io.DisplayFramebufferScale = new Vector2(_view.FramebufferSize.X / _windowWidth,
                _view.FramebufferSize.Y / _windowHeight);

        io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
    }

    private void UpdateImGuiInput()
    {
        var io = ImGuiNET.ImGui.GetIO();

        var mouseState = _input.Mice[0].CaptureState();
        var keyboardState = _input.Keyboards[0];

        io.MouseDown[0] = mouseState.IsButtonPressed(MouseButton.Left);
        io.MouseDown[1] = mouseState.IsButtonPressed(MouseButton.Right);
        io.MouseDown[2] = mouseState.IsButtonPressed(MouseButton.Middle);

        var point = new Point((int) mouseState.Position.X, (int) mouseState.Position.Y);
        io.MousePos = new Vector2(point.X, point.Y);

        var wheel = mouseState.GetScrollWheels()[0];
        io.MouseWheel = wheel.Y;
        io.MouseWheelH = wheel.X;

        foreach (Key key in Enum.GetValues(typeof(Key)))
        {
            if (key == Key.Unknown) continue;
            io.KeysDown[(int) key] = keyboardState.IsKeyPressed(key);
        }

        foreach (var c in _pressedChars) io.AddInputCharacter(c);

        _pressedChars.Clear();

        io.KeyCtrl = keyboardState.IsKeyPressed(Key.ControlLeft) || keyboardState.IsKeyPressed(Key.ControlRight);
        io.KeyAlt = keyboardState.IsKeyPressed(Key.AltLeft) || keyboardState.IsKeyPressed(Key.AltRight);
        io.KeyShift = keyboardState.IsKeyPressed(Key.ShiftLeft) || keyboardState.IsKeyPressed(Key.ShiftRight);
        io.KeySuper = keyboardState.IsKeyPressed(Key.SuperLeft) || keyboardState.IsKeyPressed(Key.SuperRight);
    }

    internal void PressChar(char keyChar)
    {
        _pressedChars.Add(keyChar);
    }

    private static void SetKeyMappings()
    {
        var io = ImGuiNET.ImGui.GetIO();
        io.KeyMap[(int) ImGuiKey.Tab] = (int) Key.Tab;
        io.KeyMap[(int) ImGuiKey.LeftArrow] = (int) Key.Left;
        io.KeyMap[(int) ImGuiKey.RightArrow] = (int) Key.Right;
        io.KeyMap[(int) ImGuiKey.UpArrow] = (int) Key.Up;
        io.KeyMap[(int) ImGuiKey.DownArrow] = (int) Key.Down;
        io.KeyMap[(int) ImGuiKey.PageUp] = (int) Key.PageUp;
        io.KeyMap[(int) ImGuiKey.PageDown] = (int) Key.PageDown;
        io.KeyMap[(int) ImGuiKey.Home] = (int) Key.Home;
        io.KeyMap[(int) ImGuiKey.End] = (int) Key.End;
        io.KeyMap[(int) ImGuiKey.Delete] = (int) Key.Delete;
        io.KeyMap[(int) ImGuiKey.Backspace] = (int) Key.Backspace;
        io.KeyMap[(int) ImGuiKey.Enter] = (int) Key.Enter;
        io.KeyMap[(int) ImGuiKey.Escape] = (int) Key.Escape;
        io.KeyMap[(int) ImGuiKey.A] = (int) Key.A;
        io.KeyMap[(int) ImGuiKey.C] = (int) Key.C;
        io.KeyMap[(int) ImGuiKey.V] = (int) Key.V;
        io.KeyMap[(int) ImGuiKey.X] = (int) Key.X;
        io.KeyMap[(int) ImGuiKey.Y] = (int) Key.Y;
        io.KeyMap[(int) ImGuiKey.Z] = (int) Key.Z;
    }

    private unsafe void RenderImDrawData(in ImDrawDataPtr drawDataPtr, in CommandBuffer commandBuffer,
        in Framebuffer framebuffer, in Extent2D swapChainExtent)
    {
        var framebufferWidth = (int) (drawDataPtr.DisplaySize.X * drawDataPtr.FramebufferScale.X);
        var framebufferHeight = (int) (drawDataPtr.DisplaySize.Y * drawDataPtr.FramebufferScale.Y);
        if (framebufferWidth <= 0 || framebufferHeight <= 0) return;

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer
        };
        renderPassInfo.RenderArea.Offset = default;
        renderPassInfo.RenderArea.Extent = swapChainExtent;
        renderPassInfo.ClearValueCount = 0;
        renderPassInfo.PClearValues = default;

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

        var drawData = *drawDataPtr.NativePtr;

        // Avoid rendering when minimized, scale coordinates for retina displays (screen coordinates != framebuffer coordinates)
        var fb_width = (int) (drawData.DisplaySize.X * drawData.FramebufferScale.X);
        var fb_height = (int) (drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fb_width <= 0 || fb_height <= 0) return;

        // Allocate array to store enough vertex/index buffers
        if (_mainWindowRenderBuffers.FrameRenderBuffers == null)
        {
            _mainWindowRenderBuffers.Index = 0;
            _mainWindowRenderBuffers.Count = (uint) _swapChainImageCt;
            _frameRenderBuffers =
                GlobalMemory.Allocate(sizeof(FrameRenderBuffer) * (int) _mainWindowRenderBuffers.Count);
            _mainWindowRenderBuffers.FrameRenderBuffers = _frameRenderBuffers.AsPtr<FrameRenderBuffer>();
            for (var i = 0; i < (int) _mainWindowRenderBuffers.Count; i++)
            {
                _mainWindowRenderBuffers.FrameRenderBuffers[i].IndexBuffer.Handle = 0;
                _mainWindowRenderBuffers.FrameRenderBuffers[i].IndexBufferSize = 0;
                _mainWindowRenderBuffers.FrameRenderBuffers[i].IndexBufferMemory.Handle = 0;
                _mainWindowRenderBuffers.FrameRenderBuffers[i].VertexBuffer.Handle = 0;
                _mainWindowRenderBuffers.FrameRenderBuffers[i].VertexBufferSize = 0;
                _mainWindowRenderBuffers.FrameRenderBuffers[i].VertexBufferMemory.Handle = 0;
            }
        }

        _mainWindowRenderBuffers.Index = (_mainWindowRenderBuffers.Index + 1) % _mainWindowRenderBuffers.Count;

        ref var frameRenderBuffer = ref _mainWindowRenderBuffers.FrameRenderBuffers[_mainWindowRenderBuffers.Index];

        if (drawData.TotalVtxCount > 0)
        {
            // Create or resize the vertex/index buffers
            var vertex_size = (ulong) drawData.TotalVtxCount * (ulong) sizeof(ImDrawVert);
            var index_size = (ulong) drawData.TotalIdxCount * sizeof(ushort);
            if (frameRenderBuffer.VertexBuffer.Handle == default || frameRenderBuffer.VertexBufferSize < vertex_size)
                CreateOrResizeBuffer(ref frameRenderBuffer.VertexBuffer, ref frameRenderBuffer.VertexBufferMemory,
                    ref frameRenderBuffer.VertexBufferSize, vertex_size, BufferUsageFlags.VertexBufferBit);
            if (frameRenderBuffer.IndexBuffer.Handle == default || frameRenderBuffer.IndexBufferSize < index_size)
                CreateOrResizeBuffer(ref frameRenderBuffer.IndexBuffer, ref frameRenderBuffer.IndexBufferMemory,
                    ref frameRenderBuffer.IndexBufferSize, index_size, BufferUsageFlags.IndexBufferBit);

            // Upload vertex/index data into a single contiguous GPU buffer
            ImDrawVert* vtx_dst = null;
            ushort* idx_dst = null;
            if (_vk.MapMemory(_device, frameRenderBuffer.VertexBufferMemory, 0, frameRenderBuffer.VertexBufferSize, 0,
                    (void**) &vtx_dst) != Result.Success) throw new Exception("Unable to map device memory");
            if (_vk.MapMemory(_device, frameRenderBuffer.IndexBufferMemory, 0, frameRenderBuffer.IndexBufferSize, 0,
                    (void**) &idx_dst) != Result.Success) throw new Exception("Unable to map device memory");
            for (var n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmd_list = drawData.CmdLists[n];
                Unsafe.CopyBlock(vtx_dst, cmd_list->VtxBuffer.Data.ToPointer(),
                    (uint) cmd_list->VtxBuffer.Size * (uint) sizeof(ImDrawVert));
                Unsafe.CopyBlock(idx_dst, cmd_list->IdxBuffer.Data.ToPointer(),
                    (uint) cmd_list->IdxBuffer.Size * sizeof(ushort));
                vtx_dst += cmd_list->VtxBuffer.Size;
                idx_dst += cmd_list->IdxBuffer.Size;
            }

            Span<MappedMemoryRange> range = stackalloc MappedMemoryRange[2];
            range[0].SType = StructureType.MappedMemoryRange;
            range[0].Memory = frameRenderBuffer.VertexBufferMemory;
            range[0].Size = Vk.WholeSize;
            range[1].SType = StructureType.MappedMemoryRange;
            range[1].Memory = frameRenderBuffer.IndexBufferMemory;
            range[1].Size = Vk.WholeSize;
            if (_vk.FlushMappedMemoryRanges(_device, 2, range) != Result.Success)
                throw new Exception("Unable to flush memory to device");
            _vk.UnmapMemory(_device, frameRenderBuffer.VertexBufferMemory);
            _vk.UnmapMemory(_device, frameRenderBuffer.IndexBufferMemory);
        }

        // Setup desired Vulkan state
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, _descriptorSet, 0,
            null);

        // Bind Vertex And Index Buffer:
        if (drawData.TotalVtxCount > 0)
        {
            ReadOnlySpan<Buffer> vertex_buffers = stackalloc Buffer[] {frameRenderBuffer.VertexBuffer};
            ulong vertex_offset = 0;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, vertex_buffers, (ulong*) Unsafe.AsPointer(ref vertex_offset));
            _vk.CmdBindIndexBuffer(commandBuffer, frameRenderBuffer.IndexBuffer, 0,
                sizeof(ushort) == 2 ? IndexType.Uint16 : IndexType.Uint32);
        }

        // Setup viewport:
        Viewport viewport;
        viewport.X = 0;
        viewport.Y = 0;
        viewport.Width = fb_width;
        viewport.Height = fb_height;
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        // Setup scale and translation:
        // Our visible imgui space lies from draw_data.DisplayPps (top left) to draw_data.DisplayPos+data_data.DisplaySize (bottom right). DisplayPos is (0,0) for single viewport apps.
        Span<float> scale = stackalloc float[2];
        scale[0] = 2.0f / drawData.DisplaySize.X;
        scale[1] = 2.0f / drawData.DisplaySize.Y;
        Span<float> translate = stackalloc float[2];
        translate[0] = -1.0f - drawData.DisplayPos.X * scale[0];
        translate[1] = -1.0f - drawData.DisplayPos.Y * scale[1];
        _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, sizeof(float) * 0,
            sizeof(float) * 2, scale);
        _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, sizeof(float) * 2,
            sizeof(float) * 2, translate);

        // Will project scissor/clipping rectangles into framebuffer space
        var clipOff = drawData.DisplayPos; // (0,0) unless using multi-viewports
        var clipScale = drawData.FramebufferScale; // (1,1) unless using retina display which are often (2,2)

        // Render command lists
        // (Because we merged all buffers into a single one, we maintain our own offset into them)
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            ref var cmd_list = ref drawData.CmdLists[n];
            for (var cmd_i = 0; cmd_i < cmd_list->CmdBuffer.Size; cmd_i++)
            {
                ref var pcmd = ref cmd_list->CmdBuffer.Ref<ImDrawCmd>(cmd_i);

                // Project scissor/clipping rectangles into framebuffer space
                Vector4 clipRect;
                clipRect.X = (pcmd.ClipRect.X - clipOff.X) * clipScale.X;
                clipRect.Y = (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
                clipRect.Z = (pcmd.ClipRect.Z - clipOff.X) * clipScale.X;
                clipRect.W = (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y;

                if (clipRect.X < fb_width && clipRect.Y < fb_height && clipRect.Z >= 0.0f && clipRect.W >= 0.0f)
                {
                    // Negative offsets are illegal for vkCmdSetScissor
                    if (clipRect.X < 0.0f)
                        clipRect.X = 0.0f;
                    if (clipRect.Y < 0.0f)
                        clipRect.Y = 0.0f;

                    // Apply scissor/clipping rectangle
                    var scissor = new Rect2D();
                    scissor.Offset.X = (int) clipRect.X;
                    scissor.Offset.Y = (int) clipRect.Y;
                    scissor.Extent.Width = (uint) (clipRect.Z - clipRect.X);
                    scissor.Extent.Height = (uint) (clipRect.W - clipRect.Y);
                    _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                    // Draw
                    _vk.CmdDrawIndexed(commandBuffer, pcmd.ElemCount, 1, pcmd.IdxOffset + (uint) indexOffset,
                        (int) pcmd.VtxOffset + vertexOffset, 0);
                }
            }

            indexOffset += cmd_list->IdxBuffer.Size;
            vertexOffset += cmd_list->VtxBuffer.Size;
        }

        _vk.CmdEndRenderPass(commandBuffer);
    }

    private unsafe void CreateOrResizeBuffer(ref Buffer buffer, ref DeviceMemory buffer_memory, ref ulong bufferSize,
        ulong newSize, BufferUsageFlags usage)
    {
        if (buffer.Handle != default) _vk.DestroyBuffer(_device, buffer, default);
        if (buffer_memory.Handle != default) _vk.FreeMemory(_device, buffer_memory, default);

        var sizeAlignedVertexBuffer = ((newSize - 1) / _bufferMemoryAlignment + 1) * _bufferMemoryAlignment;
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = sizeAlignedVertexBuffer,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        if (_vk.CreateBuffer(_device, bufferInfo, default, out buffer) != Result.Success)
            throw new Exception("Unable to create a device buffer");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var req);
        _bufferMemoryAlignment = _bufferMemoryAlignment > req.Alignment ? _bufferMemoryAlignment : req.Alignment;
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = GetMemoryTypeIndex(_vk, MemoryPropertyFlags.HostVisibleBit, req.MemoryTypeBits)
        };
        if (_vk.AllocateMemory(_device, &allocInfo, default, out buffer_memory) != Result.Success)
            throw new Exception("Unable to allocate device memory");

        if (_vk.BindBufferMemory(_device, buffer, buffer_memory, 0) != Result.Success)
            throw new Exception("Unable to bind device memory");
        bufferSize = req.Size;
    }

    private struct FrameRenderBuffer
    {
        public DeviceMemory VertexBufferMemory;
        public DeviceMemory IndexBufferMemory;
        public ulong VertexBufferSize;
        public ulong IndexBufferSize;
        public Buffer VertexBuffer;
        public Buffer IndexBuffer;
    }

    private unsafe struct WindowRenderBuffers
    {
        public uint Index;
        public uint Count;
        public FrameRenderBuffer* FrameRenderBuffers;
    }
}