﻿using System;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Text;
using Microfisher.Snowflake.Services.Dtos;
using System.Linq;
using Microfisher.Snowflake.Core.Utilities;
using Microfisher.Snowflake.Core.Responses;
using Microfisher.Snowflake.Core.Extensions;
using Microfisher.Snowflake.Data.Repositories;
using Microfisher.Snowflake.Services.Enums;
using Microfisher.Snowflake.Services.Models;
using Microfisher.Snowflake.Services.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using Microfisher.Snowflake.Data.Extenstions;
using Microfisher.Snowflake.Data.Entities;
using Microsoft.IdentityModel.Tokens;
using Microfisher.Snowflake.Services.Consts;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microfisher.Snowflake.Core.Configurations;

namespace Microfisher.Snowflake.Services
{
    /// <summary>
    /// 身份认证服务
    /// </summary>
    public class AccountService : IAccountService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly AppSetting _appSettings;
        private readonly ILogger<AccountService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IRepository<User, long> _userRepository;
        private readonly IRepository<UserBalance, long> _balanceRepository;

        public AccountService(IOptions<AppSetting> appSettings, ILogger<AccountService> logger, IHttpContextAccessor httpContextAccessor, IUnitOfWork unitOfWork, IRepository<User, long> userRepository, IRepository<UserBalance, long> balanceRepository)
        {
            _logger = logger;
            _unitOfWork = unitOfWork;
            _appSettings = appSettings.Value;
            _httpContextAccessor = httpContextAccessor;
            _userRepository = userRepository;
            _balanceRepository = balanceRepository;
        }

        /// <summary>
        /// 账号登陆
        /// </summary>
        public async Task<IResultObject> SignIn(SignInDto dto)
        {
            dto.Account = dto.Account.Trim();
            dto.Password = dto.Password.Trim();
            if (string.IsNullOrWhiteSpace(dto.Account) || string.IsNullOrWhiteSpace(dto.Password))
            {
                return new ResultObject(1, "账号及密码不能为空");
            }

            if (!ValidateHelper.IsUserName(dto.Account))
            {
                return new ResultObject(2, "账号必须是1-16位字母与数字组合");
            }

            if (!ValidateHelper.IsPassword(dto.Password))
            {
                return new ResultObject(3, "密码必须是6-16位字母与数字组合");
            }

            var token = string.Empty;
            var nowTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                // 获取用户状态
                var redisInfo = RedisHelper.HMGet(StringConst.IDENTITY_USER_INFO + dto.Account, new string[] { "SignInLockedTime", "SignInFailedCount" });
                var signInLockedTime = Convert.ToInt64(redisInfo[0]);
                var signInFailedCount = Convert.ToInt64(redisInfo[1]);
                if (redisInfo.Any(m => m == null))
                {
                    return new ResultObject(4, "用户状态不存在");
                }

                // 密码错误10次锁定5分钟
                var lockedPassTime = signInLockedTime - nowTime;
                var lockedLeftSecond = lockedPassTime % 60;
                var lockedLeftMinute = (lockedPassTime - lockedLeftSecond) / 60;
                if (signInLockedTime > nowTime)
                {
                    return new ResultObject(5, $"密码错误超过{NumberConst.IDENTITY_SIGN_IN_FAILED_COUNT}次，请稍等{lockedLeftMinute}分{lockedLeftSecond}秒后再试");
                }
                if (signInFailedCount >= NumberConst.IDENTITY_SIGN_IN_FAILED_COUNT)
                {
                    RedisHelper.HMSet(StringConst.IDENTITY_USER_INFO + dto.Account, new object[] { "SignInFailedCount", "0", "SignInLockedTime", nowTime + NumberConst.IDENTITY_SIGN_IN_LOCKED_SECOND });
                    return new ResultObject(6, "账号或密码验证失败");
                }

                // 获取账号信息
                var user = await _userRepository.GetUserByAccount(dto.Account);
                if (user.IsDisable)
                {
                    return new ResultObject(7, "账号因恶意操作被禁用");
                }
                if (user.Level >= (long)LevelEnum.Supervisor)
                {
                    return new ResultObject(8, "你没有权限执行此操作");
                }

                // 验证密码
                if (!EncryptHelper.Md5Encrypt(dto.Password).Equals(user.Password))
                {
                    RedisHelper.HIncrBy(StringConst.IDENTITY_USER_INFO + dto.Account, "SignInFailedCount");
                    return new ResultObject(9, "账号或密码验证失败");
                }

                // 授权认证
                var claims = new ClaimsIdentity(new Claim[]
                {
                    new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                    new Claim(JwtRegisteredClaimNames.Typ, user.Level.ToString()),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                });

                token = await Authorize(claims, nowTime);
            }
            catch (Exception ex)
            {
                _logger.LogError("账号登陆出错：{0}", ex.ToString());
                ExceptionHelper.Throw(ex.Message);
            }

            return new ResultObject(0, token);
        }


        /// <summary>
        /// 账号注册
        /// </summary>
        public async Task<IResultObject> SignUp(SignUpDto dto)
        {
            // 验证基础信息
            dto.Account = dto.Account.Trim();
            dto.Password = dto.Password.Trim();
            if (string.IsNullOrWhiteSpace(dto.Account) || string.IsNullOrWhiteSpace(dto.Password))
            {
                return new ResultObject(1, "账户注册信息均不能为空");
            }
            if (!ValidateHelper.IsUserName(dto.Account))
            {
                return new ResultObject(2, "账号必须是1-16位字母与数字组合");
            }
            if (!ValidateHelper.IsPassword(dto.Password))
            {
                return new ResultObject(3, "密码必须是6-16位字母与数字组合");
            }

            var user = new User();
            try
            {
                await _unitOfWork.BeginAsync();

                // 设置账号信息
                user.Level = (sbyte)LevelEnum.Beginner;
                user.Account = dto.Account;
                user.Password = EncryptHelper.Md5Encrypt(dto.Password);
                user.IpAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress.ToString();
                user.SignUpTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // 判断账户存在
                var isExist = await _userRepository.IsUserExist(dto.Account);
                if (isExist)
                {
                    return new ResultObject(4, "此账号已被注册");
                }

                // 创建账号信息
                user.Id = await _userRepository.Create(user);

                // 创建账号额度
                await _balanceRepository.Create(new UserBalance { Id = user.Id, Credit = 0, Balance = 0 }, true);

                // 写入缓存信息
                if (!RedisSetUser(user))
                {
                    await _unitOfWork.RollbackAsync();
                    RedisRollBack(user);
                }
                else
                {
                    await _unitOfWork.CommitAsync();
                }
            }
            catch (Exception ex)
            {
                await _unitOfWork.RollbackAsync();
                RedisRollBack(user);
                _logger.LogError("注册账号出错：{0}", ex.ToString());
                ExceptionHelper.Throw(ex.Message);
            }

            return new ResultObject(0, user.Id);
        }

        /// <summary>
        /// 注销账号
        /// </summary>
        public async Task<IResultObject> SignOut()
        {
            var userId = _httpContextAccessor.HttpContext.User.GetUserId();

            try
            {
                await RedisHelper.DelAsync($"{StringConst.IDENTITY_VERIFY_TOKEN}{userId}");
            }
            catch (Exception ex)
            {
                _logger.LogError("注销账号出错：{0}", ex.ToString());
                ExceptionHelper.Throw(ex.Message);
            }

            return new ResultObject(0);
        }

        /// <summary>
        /// 获取账号Token
        /// </summary>
        public async Task<IResultObject> GetJwtToken()
        {
            var id = _httpContextAccessor.HttpContext.User.GetUserId();
            var level = _httpContextAccessor.HttpContext.User.GetUserLevel();
            var token = _httpContextAccessor.HttpContext.User.GetUserToken();
            var claims = new ClaimsIdentity(new Claim[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, id.ToString()),
                new Claim(JwtRegisteredClaimNames.Typ, level.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, token),
            });

            var identity = await GetJwtToken(claims);

            return new ResultObject(0, identity);
        }

        /// <summary>
        /// 获取Token字符串
        /// </summary>
        private Task<string> GetJwtToken(ClaimsIdentity claims)
        {
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = claims,
                Expires = DateTime.UtcNow.AddDays(_appSettings.Jwt.Expired),
                //Issuer = _appSettings.Jwt.Issuer,
                //Audience = _appSettings.Jwt.Audience,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_appSettings.Jwt.Secret)), SecurityAlgorithms.HmacSha256)
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            var securityToken = tokenHandler.CreateToken(tokenDescriptor);
            var token = tokenHandler.WriteToken(securityToken);
            return Task.FromResult(token);
        }


        /// <summary>
        /// 授权认证
        /// </summary>
        private async Task<string> Authorize(ClaimsIdentity claims, long nowTime)
        {
            var token = string.Empty;
            try
            {
                await _unitOfWork.BeginAsync();

                var id = Convert.ToInt64(claims.FindFirst(JwtRegisteredClaimNames.Sub).Value);
                var identity = claims.FindFirst(JwtRegisteredClaimNames.Jti).Value;
                token = await GetJwtToken(claims);

                // 更新登陆状态
                var ipAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress.ToString();
                await _userRepository.Update(new { Id = id, SignInTime = nowTime, IpAddress = ipAddress });

                // 设置身份识别
                RedisHelper.Set($"{StringConst.IDENTITY_VERIFY_TOKEN}{id}", identity, NumberConst.IDENTITY_SIGN_IN_STATE_EXPIRED);

                await _unitOfWork.CommitAsync();
            }
            catch (Exception ex)
            {
                await _unitOfWork.RollbackAsync();
                _logger.LogError("登陆授权出错：" + ex);
                ExceptionHelper.Throw(ex.Message);
            }

            return token;
        }

        /// <summary>
        ///  创建用户缓存
        /// </summary>
        private bool RedisSetUser(User user)
        {
            return RedisHelper.HMSet(StringConst.IDENTITY_USER_INFO + user.Account, new object[] { "Id", user.Id, "SignInFailedCount", "0", "SignInLockedTime", "0" });
        }

        /// <summary>
        /// 回滚Redis缓存
        /// </summary>
        private void RedisRollBack(User user)
        {
            RedisHelper.Del(StringConst.IDENTITY_USER_INFO + user.Account);
        }
    }
}
