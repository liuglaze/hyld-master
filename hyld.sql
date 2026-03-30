/*
SQLyog Ultimate v10.00 Beta1
MySQL - 5.5.62 : Database - hyld
*********************************************************************
*/

/*!40101 SET NAMES utf8 */;

/*!40101 SET SQL_MODE=''*/;

/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;
CREATE DATABASE /*!32312 IF NOT EXISTS*/`hyld` /*!40100 DEFAULT CHARACTER SET utf8 */;

USE `hyld`;

/*Table structure for table `friends` */

DROP TABLE IF EXISTS `friends`;

CREATE TABLE `friends` (
  `UserID` INT(10) NOT NULL,
  `FriendID` INT(10) NOT NULL,
  KEY `UserID` (`UserID`),
  KEY `FriendID` (`FriendID`),
  CONSTRAINT `friends_ibfk_1` FOREIGN KEY (`UserID`) REFERENCES `users` (`id`),
  CONSTRAINT `friends_ibfk_2` FOREIGN KEY (`FriendID`) REFERENCES `users` (`id`)
) ENGINE=INNODB DEFAULT CHARSET=utf8;

/*Data for the table `friends` */

INSERT  INTO `friends`(`UserID`,`FriendID`) VALUES (20,23),(1,20),(1,4),(20,19),(19,1),(20,12),(12,19),(12,23),(20,24),(25,20),(26,20),(23,25),(24,23),(23,24);

/*Table structure for table `hero` */

DROP TABLE IF EXISTS `hero`;

CREATE TABLE `hero` (
  `UserId` INT(11) DEFAULT NULL,
  `hero` INT(11) DEFAULT NULL COMMENT '英雄id',
  `level` INT(11) DEFAULT NULL COMMENT '英雄等级',
  `fragment` INT(11) DEFAULT NULL COMMENT '当前你有多少英雄碎片'
) ENGINE=INNODB DEFAULT CHARSET=utf8;

/*Data for the table `hero` */

/*Table structure for table `task` */

DROP TABLE IF EXISTS `task`;

CREATE TABLE `task` (
  `UserID` INT(11) DEFAULT NULL,
  `taskID` INT(11) DEFAULT NULL
) ENGINE=INNODB DEFAULT CHARSET=utf8;

/*Data for the table `task` */

/*Table structure for table `users` */

DROP TABLE IF EXISTS `users`;

CREATE TABLE `users` (
  `UserName` VARCHAR(20) NOT NULL COMMENT '账号',
  `Password` VARCHAR(20) NOT NULL COMMENT '密码',
  `name` VARCHAR(20) NOT NULL COMMENT '昵称',
  `id` INT(6) NOT NULL AUTO_INCREMENT,
  `coin` INT(11) DEFAULT NULL COMMENT '金币',
  `diamond` INT(11) DEFAULT NULL COMMENT '钻石',
  PRIMARY KEY (`UserName`),
  KEY `id` (`id`)
) ENGINE=INNODB AUTO_INCREMENT=27 DEFAULT CHARSET=utf8;

/*Data for the table `users` */

INSERT  INTO `users`(`UserName`,`Password`,`name`,`id`,`coin`,`diamond`) VALUES ('0','0','Dwadfw',24,NULL,NULL),('123','123','龙之介的狗',1,NULL,NULL),('18978668903','123456','Saujuz',26,NULL,NULL),('a','d','Hql太摸了',4,NULL,NULL),('hql','lzj','Hql',23,NULL,NULL),('lzj','hql','Qfqf',12,NULL,NULL),('xy','wgw','Wgw',19,NULL,NULL),('yxy','gg','Yxygg',20,NULL,NULL),('别离歌','123456789','别离歌',25,NULL,NULL);

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;
