using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

internal sealed unsafe class ImGuiVulkanRenderer : IDisposable
{
    private const int ImDrawVertSize = 20;
    private const int ImDrawIdxSize = 2;
    private const uint UserTextureDescriptorCapacity = 65536;

    private readonly Vk _vk;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _graphicsQueue;
    private readonly uint _graphicsQueueFamilyIndex;

    private DescriptorPool _descriptorPool;
    private DescriptorSetLayout _descriptorSetLayout;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;

    private Sampler _fontSampler;
    private Image _fontImage;
    private DeviceMemory _fontImageMemory;
    private ImageView _fontImageView;
    private DescriptorSet _fontDescriptorSet;

    private CommandPool _uploadCommandPool;

    private readonly Dictionary<ulong, TextureResources> _textures = new();

    private FrameResources[] _frames = Array.Empty<FrameResources>();
    private RenderPass _renderPass;

    private bool _disposed;

    public ImGuiVulkanRenderer(
        Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        Queue graphicsQueue,
        uint graphicsQueueFamilyIndex,
        RenderPass renderPass,
        int swapchainImageCount)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _physicalDevice = physicalDevice;
        _device = device;
        _graphicsQueue = graphicsQueue;
        _graphicsQueueFamilyIndex = graphicsQueueFamilyIndex;
        _renderPass = renderPass;

        CreateDescriptorPool();
        CreateDescriptorSetLayout();
        CreatePipelineLayout();
        CreateUploadCommandPool();
        CreateFontResources();
        CreatePipeline(_renderPass);
        ResizeFrames(swapchainImageCount);
    }

    public void DisposeSwapchainResources()
    {
        DestroyPipeline();
        DestroyFrameResources();
    }

    public void OnSwapchainRecreated(RenderPass renderPass, int swapchainImageCount)
    {
        _renderPass = renderPass;
        CreatePipeline(_renderPass);
        ResizeFrames(swapchainImageCount);
    }

    public void Render(ImDrawDataPtr drawData, CommandBuffer commandBuffer, int framebufferWidth, int framebufferHeight, int imageIndex)
    {
        if (_disposed)
        {
            return;
        }

        if (drawData.NativePtr is null)
        {
            return;
        }

        if (framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        if (drawData.TotalVtxCount <= 0 || drawData.TotalIdxCount <= 0)
        {
            return;
        }

        if (_pipeline.Handle == 0)
        {
            return;
        }

        if ((uint)imageIndex >= (uint)_frames.Length)
        {
            return;
        }

        FrameResources frame = _frames[imageIndex];

        ulong vertexBufferSize = (ulong)drawData.TotalVtxCount * ImDrawVertSize;
        ulong indexBufferSize = (ulong)drawData.TotalIdxCount * ImDrawIdxSize;

        EnsureHostBuffer(ref frame.VertexBuffer, ref frame.VertexMemory, ref frame.VertexBufferSize, vertexBufferSize, BufferUsageFlags.VertexBufferBit);
        EnsureHostBuffer(ref frame.IndexBuffer, ref frame.IndexMemory, ref frame.IndexBufferSize, indexBufferSize, BufferUsageFlags.IndexBufferBit);

        _frames[imageIndex] = frame;

        CopyDrawDataToBuffers(drawData, frame, vertexBufferSize, indexBufferSize);

        SetupRenderState(commandBuffer, drawData, framebufferWidth, framebufferHeight, frame);

        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;

        int globalVtxOffset = 0;
        uint globalIdxOffset = 0;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = GetCmdList(drawData, n);

            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdI];
                if (pcmd.UserCallback != nint.Zero)
                {
                    continue;
                }

                Vector4 clipRect = pcmd.ClipRect;
                float clipMinX = (clipRect.X - clipOff.X) * clipScale.X;
                float clipMinY = (clipRect.Y - clipOff.Y) * clipScale.Y;
                float clipMaxX = (clipRect.Z - clipOff.X) * clipScale.X;
                float clipMaxY = (clipRect.W - clipOff.Y) * clipScale.Y;

                clipMinX = Math.Clamp(clipMinX, 0.0f, framebufferWidth);
                clipMinY = Math.Clamp(clipMinY, 0.0f, framebufferHeight);
                clipMaxX = Math.Clamp(clipMaxX, 0.0f, framebufferWidth);
                clipMaxY = Math.Clamp(clipMaxY, 0.0f, framebufferHeight);

                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                {
                    continue;
                }

                Rect2D scissor = new()
                {
                    Offset = new Offset2D((int)clipMinX, (int)clipMinY),
                    Extent = new Extent2D((uint)(clipMaxX - clipMinX), (uint)(clipMaxY - clipMinY))
                };
                _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                DescriptorSet descriptorSet = GetDescriptorSetFromTextureId(pcmd.TextureId);
                _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &descriptorSet, 0, null);

                _vk.CmdDrawIndexed(
                    commandBuffer,
                    pcmd.ElemCount,
                    1,
                    pcmd.IdxOffset + globalIdxOffset,
                    unchecked((int)pcmd.VtxOffset) + globalVtxOffset,
                    0);
            }

            globalIdxOffset += (uint)cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        DestroyAllTextures();

        DisposeSwapchainResources();

        if (_fontSampler.Handle != 0)
        {
            _vk.DestroySampler(_device, _fontSampler, null);
        }
        if (_fontImageView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _fontImageView, null);
        }
        if (_fontImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _fontImage, null);
        }
        if (_fontImageMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _fontImageMemory, null);
        }

        if (_descriptorSetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
        }
        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }
        if (_descriptorPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        }

        if (_uploadCommandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _uploadCommandPool, null);
        }
    }

    internal nint CreateTextureRgba8(ReadOnlySpan<byte> rgba8, int width, int height)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ImGuiVulkanRenderer));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须大于 0");
        }

        int expectedBytes = checked(width * height * 4);
        if (rgba8.Length != expectedBytes)
        {
            throw new ArgumentException($"RGBA8 数据长度不匹配: expected={expectedBytes}, actual={rgba8.Length}", nameof(rgba8));
        }

        ulong uploadSize = (ulong)expectedBytes;

        VkBuffer stagingBuffer = default;
        DeviceMemory stagingMemory = default;
        CreateBuffer(uploadSize, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out stagingBuffer, out stagingMemory);

        try
        {
            void* mapped = null;
            Result mapResult = _vk.MapMemory(_device, stagingMemory, 0, uploadSize, 0, &mapped);
            Ensure(mapResult, "vkMapMemory(stagingTexture)");

            fixed (byte* src = rgba8)
            {
                System.Buffer.MemoryCopy(src, mapped, uploadSize, uploadSize);
            }

            _vk.UnmapMemory(_device, stagingMemory);

            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.R8G8B8A8Unorm,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            Result imgResult = _vk.CreateImage(_device, &imageInfo, null, out Image image);
            Ensure(imgResult, "vkCreateImage(texture)");

            DeviceMemory imageMemory = default;
            ImageView imageView = default;
            Sampler sampler = default;
            DescriptorSet descriptorSet = default;

            try
            {
                MemoryRequirements imgReq;
                _vk.GetImageMemoryRequirements(_device, image, out imgReq);
                uint imgMemoryType = FindMemoryType(imgReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

                MemoryAllocateInfo allocInfo = new()
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = imgReq.Size,
                    MemoryTypeIndex = imgMemoryType
                };

                Result allocResult = _vk.AllocateMemory(_device, &allocInfo, null, out imageMemory);
                Ensure(allocResult, "vkAllocateMemory(texture)");

                Result bindResult = _vk.BindImageMemory(_device, image, imageMemory, 0);
                Ensure(bindResult, "vkBindImageMemory(texture)");

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = Format.R8G8B8A8Unorm,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                Result viewResult = _vk.CreateImageView(_device, &viewInfo, null, out imageView);
                Ensure(viewResult, "vkCreateImageView(texture)");

                SamplerCreateInfo samplerInfo = new()
                {
                    SType = StructureType.SamplerCreateInfo,
                    // 旧 C++ 地图/素材纹理缓存使用 GL_NEAREST。
                    // WoOOL 的房屋、树木等大物件通常由多张带透明边缘的小贴图拼接而成，
                    // 在线性采样下会把 alpha=0 边缘的 RGB 一起插值出来，表现为黑虚线、
                    // 房屋切片之间漏底色、或者像“又把地面贴了一遍”。
                    // Vulkan 这里改回最近点采样，和旧版行为保持一致。
                    MagFilter = Filter.Nearest,
                    MinFilter = Filter.Nearest,
                    MipmapMode = SamplerMipmapMode.Nearest,
                    AddressModeU = SamplerAddressMode.ClampToEdge,
                    AddressModeV = SamplerAddressMode.ClampToEdge,
                    AddressModeW = SamplerAddressMode.ClampToEdge,
                    MinLod = -1000,
                    MaxLod = 1000
                };

                Result samplerResult = _vk.CreateSampler(_device, &samplerInfo, null, out sampler);
                Ensure(samplerResult, "vkCreateSampler(texture)");

                descriptorSet = AllocateTextureDescriptorSet(sampler, imageView);

                UploadTexture(stagingBuffer, image, (uint)width, (uint)height);

                var resources = new TextureResources
                {
                    Image = image,
                    Memory = imageMemory,
                    View = imageView,
                    Sampler = sampler,
                    DescriptorSet = descriptorSet,
                    Width = (uint)width,
                    Height = (uint)height,
                };

                _textures[descriptorSet.Handle] = resources;
                return new nint(unchecked((long)descriptorSet.Handle));
            }
            catch
            {
                if (descriptorSet.Handle != 0)
                {
                    FreeDescriptorSet(descriptorSet);
                }

                if (sampler.Handle != 0)
                {
                    _vk.DestroySampler(_device, sampler, null);
                }

                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }

                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }

                if (imageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, imageMemory, null);
                }

                throw;
            }
        }
        finally
        {
            if (stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, stagingBuffer, null);
            }

            if (stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, stagingMemory, null);
            }
        }
    }

    internal void DestroyTexture(nint textureId)
    {
        if (_disposed)
        {
            return;
        }

        if (textureId == nint.Zero)
        {
            return;
        }

        ulong handle = unchecked((ulong)(long)textureId);
        if (!_textures.Remove(handle, out TextureResources resources))
        {
            return;
        }

        _vk.QueueWaitIdle(_graphicsQueue);
        DestroyTextureResources(resources);
    }

    private DescriptorSet GetDescriptorSetFromTextureId(nint textureId)
    {
        if (textureId == nint.Zero)
        {
            return _fontDescriptorSet;
        }

        ulong handle = unchecked((ulong)(long)textureId);
        return new DescriptorSet(handle);
    }

    private void CreateDescriptorPool()
    {
        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = UserTextureDescriptorCapacity
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = UserTextureDescriptorCapacity,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };

        Result result = _vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool);
        Ensure(result, "vkCreateDescriptorPool");
    }

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };

        DescriptorSetLayoutCreateInfo info = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };

        Result result = _vk.CreateDescriptorSetLayout(_device, &info, null, out _descriptorSetLayout);
        Ensure(result, "vkCreateDescriptorSetLayout");
    }

    private void CreatePipelineLayout()
    {
        PushConstantRange pushConstantRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)(4 * sizeof(float))
        };

        fixed (DescriptorSetLayout* setLayoutPtr = &_descriptorSetLayout)
        {
            PipelineLayoutCreateInfo info = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = setLayoutPtr,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _vk.CreatePipelineLayout(_device, &info, null, out _pipelineLayout);
            Ensure(result, "vkCreatePipelineLayout");
        }
    }

    private void CreateUploadCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamilyIndex
        };

        Result result = _vk.CreateCommandPool(_device, &poolInfo, null, out _uploadCommandPool);
        Ensure(result, "vkCreateCommandPool(upload)");
    }

    private void CreateFontResources()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        if (pixels is null || width <= 0 || height <= 0 || bytesPerPixel <= 0)
        {
            throw new InvalidOperationException("ImGui 字体贴图数据为空。");
        }

        ulong uploadSize = checked((ulong)width * (ulong)height * (ulong)bytesPerPixel);

        VkBuffer stagingBuffer = default;
        DeviceMemory stagingMemory = default;
        CreateBuffer(uploadSize, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out stagingBuffer, out stagingMemory);

        void* mapped = null;
        Result mapResult = _vk.MapMemory(_device, stagingMemory, 0, uploadSize, 0, &mapped);
        Ensure(mapResult, "vkMapMemory(staging)");
        System.Buffer.MemoryCopy(pixels, mapped, uploadSize, uploadSize);
        _vk.UnmapMemory(_device, stagingMemory);

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        Result imgResult = _vk.CreateImage(_device, &imageInfo, null, out _fontImage);
        Ensure(imgResult, "vkCreateImage(font)");

        MemoryRequirements imgReq;
        _vk.GetImageMemoryRequirements(_device, _fontImage, out imgReq);
        uint imgMemoryType = FindMemoryType(imgReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = imgReq.Size,
            MemoryTypeIndex = imgMemoryType
        };

        Result allocResult = _vk.AllocateMemory(_device, &allocInfo, null, out _fontImageMemory);
        Ensure(allocResult, "vkAllocateMemory(font)");

        Result bindResult = _vk.BindImageMemory(_device, _fontImage, _fontImageMemory, 0);
        Ensure(bindResult, "vkBindImageMemory(font)");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _fontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        Result viewResult = _vk.CreateImageView(_device, &viewInfo, null, out _fontImageView);
        Ensure(viewResult, "vkCreateImageView(font)");

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = -1000,
            MaxLod = 1000
        };

        Result samplerResult = _vk.CreateSampler(_device, &samplerInfo, null, out _fontSampler);
        Ensure(samplerResult, "vkCreateSampler(font)");

        AllocateFontDescriptorSet();

        UploadFontTexture(stagingBuffer, (uint)width, (uint)height);

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        io.Fonts.SetTexID(new nint(unchecked((long)_fontDescriptorSet.Handle)));
        io.Fonts.ClearTexData();
    }

    private void AllocateFontDescriptorSet()
    {
        fixed (DescriptorSetLayout* layoutPtr = &_descriptorSetLayout)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = layoutPtr
            };

            Result result = _vk.AllocateDescriptorSets(_device, &allocInfo, out _fontDescriptorSet);
            Ensure(result, "vkAllocateDescriptorSets(font)");
        }

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = _fontSampler,
            ImageView = _fontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _fontDescriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo
        };

        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    private DescriptorSet AllocateTextureDescriptorSet(Sampler sampler, ImageView view)
    {
        DescriptorSet set;

        fixed (DescriptorSetLayout* layoutPtr = &_descriptorSetLayout)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = layoutPtr
            };

            Result result = _vk.AllocateDescriptorSets(_device, &allocInfo, out set);
            Ensure(result, "vkAllocateDescriptorSets(texture)");
        }

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo
        };

        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        return set;
    }

    private void FreeDescriptorSet(DescriptorSet set)
    {
        if (set.Handle == 0)
        {
            return;
        }

        DescriptorSet ds = set;
        _vk.FreeDescriptorSets(_device, _descriptorPool, 1, &ds);
    }

    private void UploadFontTexture(VkBuffer stagingBuffer, uint width, uint height)
    {
        UploadTexture(stagingBuffer, _fontImage, width, height);
    }

    private void UploadTexture(VkBuffer stagingBuffer, Image image, uint width, uint height)
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _uploadCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        CommandBuffer commandBuffer;
        Result allocResult = _vk.AllocateCommandBuffers(_device, &allocInfo, out commandBuffer);
        Ensure(allocResult, "vkAllocateCommandBuffers(upload)");

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        Result beginResult = _vk.BeginCommandBuffer(commandBuffer, &beginInfo);
        Ensure(beginResult, "vkBeginCommandBuffer(upload)");

        TransitionImageLayout(commandBuffer, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

        BufferImageCopy region = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };

        _vk.CmdCopyBufferToImage(commandBuffer, stagingBuffer, image, ImageLayout.TransferDstOptimal, 1, &region);

        TransitionImageLayout(commandBuffer, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);

        Result endResult = _vk.EndCommandBuffer(commandBuffer);
        Ensure(endResult, "vkEndCommandBuffer(upload)");

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo
        };

        Fence fence;
        Result fenceResult = _vk.CreateFence(_device, &fenceInfo, null, out fence);
        Ensure(fenceResult, "vkCreateFence(upload)");

        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        Result submitResult = _vk.QueueSubmit(_graphicsQueue, 1, &submit, fence);
        Ensure(submitResult, "vkQueueSubmit(upload)");

        _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);

        _vk.DestroyFence(_device, fence, null);
        _vk.FreeCommandBuffers(_device, _uploadCommandPool, 1, &commandBuffer);
    }

    private void TransitionImageLayout(CommandBuffer commandBuffer, Image image, ImageLayout oldLayout, ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags sourceStage;
        PipelineStageFlags destinationStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new NotSupportedException($"不支持的 image layout 变换: {oldLayout} -> {newLayout}");
        }

        _vk.CmdPipelineBarrier(
            commandBuffer,
            sourceStage,
            destinationStage,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private void DestroyAllTextures()
    {
        if (_textures.Count == 0)
        {
            return;
        }

        _vk.QueueWaitIdle(_graphicsQueue);

        foreach (TextureResources resources in _textures.Values)
        {
            DestroyTextureResources(resources);
        }

        _textures.Clear();
    }

    private void DestroyTextureResources(TextureResources resources)
    {
        if (resources.Sampler.Handle != 0)
        {
            _vk.DestroySampler(_device, resources.Sampler, null);
        }

        if (resources.View.Handle != 0)
        {
            _vk.DestroyImageView(_device, resources.View, null);
        }

        if (resources.Image.Handle != 0)
        {
            _vk.DestroyImage(_device, resources.Image, null);
        }

        if (resources.Memory.Handle != 0)
        {
            _vk.FreeMemory(_device, resources.Memory, null);
        }

        if (resources.DescriptorSet.Handle != 0)
        {
            FreeDescriptorSet(resources.DescriptorSet);
        }
    }

    private void CreatePipeline(RenderPass renderPass)
    {
        DestroyPipeline();

        byte* entryPoint = stackalloc byte[5];
        entryPoint[0] = (byte)'m';
        entryPoint[1] = (byte)'a';
        entryPoint[2] = (byte)'i';
        entryPoint[3] = (byte)'n';
        entryPoint[4] = 0;

        fixed (uint* vertCodePtr = ImGuiVulkanShaders.VertexShaderSpv)
        fixed (uint* fragCodePtr = ImGuiVulkanShaders.FragmentShaderSpv)
        {
            ShaderModule vertModule = CreateShaderModule(vertCodePtr, (nuint)(ImGuiVulkanShaders.VertexShaderSpv.Length * sizeof(uint)));
            ShaderModule fragModule = CreateShaderModule(fragCodePtr, (nuint)(ImGuiVulkanShaders.FragmentShaderSpv.Length * sizeof(uint)));

            try
            {
                PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertModule,
                    PName = entryPoint
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragModule,
                    PName = entryPoint
                };

                VertexInputBindingDescription bindingDesc = new()
                {
                    Binding = 0,
                    Stride = (uint)ImDrawVertSize,
                    InputRate = VertexInputRate.Vertex
                };

                VertexInputAttributeDescription* attributeDesc = stackalloc VertexInputAttributeDescription[3];
                attributeDesc[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 };
                attributeDesc[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 };
                attributeDesc[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 16 };

                PipelineVertexInputStateCreateInfo vertexInputInfo = new()
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &bindingDesc,
                    VertexAttributeDescriptionCount = 3,
                    PVertexAttributeDescriptions = attributeDesc
                };

                PipelineInputAssemblyStateCreateInfo inputAssembly = new()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                    PrimitiveRestartEnable = false
                };

                PipelineViewportStateCreateInfo viewportState = new()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };

                PipelineRasterizationStateCreateInfo rasterizer = new()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1.0f
                };

                PipelineMultisampleStateCreateInfo multisampling = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                PipelineColorBlendAttachmentState colorBlendAttachment = new()
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.SrcAlpha,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    DstAlphaBlendFactor = BlendFactor.One,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };

                PipelineColorBlendStateCreateInfo colorBlending = new()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };

                DynamicState* dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                PipelineDynamicStateCreateInfo dynamicState = new()
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates
                };

                GraphicsPipelineCreateInfo pipelineInfo = new()
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInputInfo,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisampling,
                    PColorBlendState = &colorBlending,
                    PDynamicState = &dynamicState,
                    Layout = _pipelineLayout,
                    RenderPass = renderPass,
                    Subpass = 0
                };

                Result result = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipeline);
                Ensure(result, "vkCreateGraphicsPipelines(ImGui)");
            }
            finally
            {
                _vk.DestroyShaderModule(_device, vertModule, null);
                _vk.DestroyShaderModule(_device, fragModule, null);
            }
        }
    }

    private ShaderModule CreateShaderModule(uint* code, nuint codeSize)
    {
        ShaderModuleCreateInfo createInfo = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = codeSize,
            PCode = code
        };

        ShaderModule module;
        Result result = _vk.CreateShaderModule(_device, &createInfo, null, out module);
        Ensure(result, "vkCreateShaderModule");
        return module;
    }

    private void DestroyPipeline()
    {
        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
            _pipeline = default;
        }
    }

    private void ResizeFrames(int swapchainImageCount)
    {
        if (swapchainImageCount <= 0)
        {
            DestroyFrameResources();
            _frames = Array.Empty<FrameResources>();
            return;
        }

        if (_frames.Length == swapchainImageCount)
        {
            return;
        }

        DestroyFrameResources();
        _frames = new FrameResources[swapchainImageCount];
    }

    private void DestroyFrameResources()
    {
        if (_frames.Length == 0)
        {
            return;
        }

        for (int i = 0; i < _frames.Length; i++)
        {
            DestroyBuffer(ref _frames[i].VertexBuffer, ref _frames[i].VertexMemory);
            DestroyBuffer(ref _frames[i].IndexBuffer, ref _frames[i].IndexMemory);
            _frames[i].VertexBufferSize = 0;
            _frames[i].IndexBufferSize = 0;
        }
    }

    private void DestroyBuffer(ref VkBuffer buffer, ref DeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, buffer, null);
            buffer = default;
        }
        if (memory.Handle != 0)
        {
            _vk.FreeMemory(_device, memory, null);
            memory = default;
        }
    }

    private void CopyDrawDataToBuffers(ImDrawDataPtr drawData, FrameResources frame, ulong vertexBufferSize, ulong indexBufferSize)
    {
        void* vtxDst = null;
        void* idxDst = null;

        Result mapVtx = _vk.MapMemory(_device, frame.VertexMemory, 0, vertexBufferSize, 0, &vtxDst);
        Ensure(mapVtx, "vkMapMemory(vtx)");

        Result mapIdx = _vk.MapMemory(_device, frame.IndexMemory, 0, indexBufferSize, 0, &idxDst);
        Ensure(mapIdx, "vkMapMemory(idx)");

        try
        {
            ulong vtxOffset = 0;
            ulong idxOffset = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = GetCmdList(drawData, n);

                ulong vtxBytes = (ulong)cmdList.VtxBuffer.Size * ImDrawVertSize;
                ulong idxBytes = (ulong)cmdList.IdxBuffer.Size * ImDrawIdxSize;

                System.Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, (byte*)vtxDst + vtxOffset, vertexBufferSize - vtxOffset, vtxBytes);
                System.Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, (byte*)idxDst + idxOffset, indexBufferSize - idxOffset, idxBytes);

                vtxOffset += vtxBytes;
                idxOffset += idxBytes;
            }
        }
        finally
        {
            _vk.UnmapMemory(_device, frame.VertexMemory);
            _vk.UnmapMemory(_device, frame.IndexMemory);
        }
    }

    private void SetupRenderState(CommandBuffer commandBuffer, ImDrawDataPtr drawData, int framebufferWidth, int framebufferHeight, FrameResources frame)
    {
        Vector2 displaySize = drawData.DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            return;
        }

        Viewport viewport = new()
        {
            X = 0,
            Y = 0,
            Width = framebufferWidth,
            Height = framebufferHeight,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);

        VkBuffer vertexBuffer = frame.VertexBuffer;
        ulong vertexOffset = 0;
        _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &vertexOffset);

        _vk.CmdBindIndexBuffer(commandBuffer, frame.IndexBuffer, 0, IndexType.Uint16);

        float scaleX = 2.0f / displaySize.X;
        float scaleY = 2.0f / displaySize.Y;
        float translateX = -1.0f - drawData.DisplayPos.X * scaleX;
        float translateY = -1.0f - drawData.DisplayPos.Y * scaleY;

        float* pushConstants = stackalloc float[4] { scaleX, scaleY, translateX, translateY };
        _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)(4 * sizeof(float)), pushConstants);
    }

    private void EnsureHostBuffer(ref VkBuffer buffer, ref DeviceMemory memory, ref ulong currentSize, ulong requiredSize, BufferUsageFlags usage)
    {
        if (requiredSize == 0)
        {
            return;
        }

        if (buffer.Handle != 0 && currentSize >= requiredSize)
        {
            return;
        }

        DestroyBuffer(ref buffer, ref memory);

        ulong newSize = requiredSize;
        CreateBuffer(newSize, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out buffer, out memory);
        currentSize = newSize;
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out VkBuffer buffer, out DeviceMemory memory)
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        Result result = _vk.CreateBuffer(_device, &bufferInfo, null, out buffer);
        Ensure(result, "vkCreateBuffer");

        MemoryRequirements memRequirements;
        _vk.GetBufferMemoryRequirements(_device, buffer, out memRequirements);

        uint memoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex
        };

        result = _vk.AllocateMemory(_device, &allocInfo, null, out memory);
        Ensure(result, "vkAllocateMemory(buffer)");

        result = _vk.BindBufferMemory(_device, buffer, memory, 0);
        Ensure(result, "vkBindBufferMemory");
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProperties;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            bool typeMatches = (typeFilter & (1u << (int)i)) != 0;
            bool propsMatch = (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties;
            if (typeMatches && propsMatch)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"未找到匹配的内存类型（typeFilter=0x{typeFilter:x8}, props={properties}）。");
    }

    private static void Ensure(Result result, string action)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{action} 失败: {result}");
        }
    }

    private static ImDrawListPtr GetCmdList(ImDrawDataPtr drawData, int index)
    {
        return drawData.CmdLists[index];
    }

    private struct FrameResources
    {
        public VkBuffer VertexBuffer;
        public DeviceMemory VertexMemory;
        public ulong VertexBufferSize;

        public VkBuffer IndexBuffer;
        public DeviceMemory IndexMemory;
        public ulong IndexBufferSize;
    }

    private struct TextureResources
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
        public DescriptorSet DescriptorSet;
        public uint Width;
        public uint Height;
    }
}
