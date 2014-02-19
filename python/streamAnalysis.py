import numpy
import cv2
from livestreamer import Livestreamer

import os
import sys
import collections
import pickle

import time
from threading import Thread


overridePosition = (-1, -1)

class MapGeneration( Thread ):

  def __init__( self, timeList, eventList, worldmap ):
    Thread.__init__( self )
    self._timeList = timeList
    self._eventList = eventList
    self._worldmap = worldmap

  def run( self ):
    time.sleep(60)
    while True:
      worldmap = self._worldmap.copy()
      worldmapCopy = self._worldmap.copy()

      with open('positionData.csv', 'r') as _file:
        entries = _file.readlines()
        for entry in entries:
          timeStamp, day, hour, minute, second, x, y = entry.strip().split(';')
          cv2.circle(worldmap, (int(x), int(y)), 10, (129, 255, 59), -1)
          cv2.circle(worldmap, (int(x), int(y)), 10, (0, 0, 0), 1)

      if not x:
        time.sleep(60)
        continue      
 
      cv2.addWeighted(worldmap, 0.4, worldmapCopy, 1 - 0.4, 0, worldmapCopy)
      cropped = worldmapCopy[int(y)-250:int(y)+250, int(x)-500:int(x)+500]
      cv2.imwrite( 'mapcrop.png', cropped )

      cv2.circle(worldmapCopy, (int(x), int(y)), 20, (0, 0, 255), -1)
      cv2.imwrite( 'maptest.png', worldmapCopy )

      os.system('./pngcrush maptest.png mapcompressed.png')
      os.system('./pngcrush mapcrop.png mapcropcomp.png')

      os.system('cp mapcompressed.png www/map.png')
      os.system('cp mapcropcomp.png www/mapcrop.png')

      with open( 'www/conversionCenter', 'w' ) as _file:
        _file.write('{} {}'.format(x, y))

      time.sleep(400)


class StreamAnalysis( Thread ):

  def __init__( self, timeList, eventList, worldmap ):
    Thread.__init__( self )
    self._timeList = timeList
    self._eventList = eventList

    self._timeZero = 1392254560

    # set up templates as private variables for this class
    self._templatesTime = pickle.load( open('timeValues.data', 'rb') )
    self._templatesRed = pickle.load( open('redTemplates.data', 'rb') )
    self._templatesEvent = pickle.load( open('eventTemplates.data', 'rb') )

    self._worldmap = cv2.cvtColor(worldmap.copy(), cv2.COLOR_BGR2GRAY)


  def initStream( self ):
    self._livestreamer = Livestreamer()
    self._plugin = self._livestreamer.resolve_url('http://twitch.tv/twitchplayspokemon')

  def fetchStream( self ):
    streams = self._plugin.get_streams()
    if 'source' in streams:
      return streams.get('source')

    return False

  def run( self ):
    
    self.initStream()
    
    stream = self.fetchStream()
    while stream == False:
      print 'STREAM'
      time.sleep(60)
      print 'rechecking if stream is available'
      stream = self.fetchStream()

    #cap = cv2.VideoCapture( 'http://store22.media22.justin.tv/archives/2014-2-17/live_user_twitchplayspokemon_1392633446.flv' )
    cap = cv2.VideoCapture( stream.url )

    tmpTimeValues = []
    last_position = (-1, -1)

    while True:

      if not overridePosition == (-1, -1) and last_position == (-1, -1):
        last_position = overridePosition

      flag, frame = cap.read()
      
      if not flag:
        stream = self.fetchStream()
        cap = cv2.VideoCapture( stream.url )
        flag, frame = cap.read()

        if not flag:
          break

      gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
      timeValues = self.getTimeValues( gray[81:119, 526:830] )

      if timeValues == tmpTimeValues:
        continue

      tmpTimeValues = timeValues
      date = self.getDateFromTime( timeValues )
      if not 'day' in date or not 'hour' in date or not 'minute' in date or not 'second' in date:
        print 'error parsing date'
        continue

      timeStamp = self._timeZero + int(date['day']) * 24 * 60 * 60 + int(date['hour']) * 60 * 60 + int(date['minute']) * 60 + int(date['second'])

      # crop to gameboy area
      crop = gray[24:457, 24:504]

      # find red (if not found with enough confidence it's "undefined")
      currentDirection = self.getRedDirection( crop[182:230, 194:233] )
      if currentDirection == 'undefined':
        #self.newEntry(timeStamp, date, last_position)
        continue

      # check if there is currently an event open
      events = self.detectEvents( crop )
      if len(events) > 0 and not last_position == (-1, -1):
        #self.newEntry(timeStamp, date, last_position)
        self._eventList.append(events[0])
        continue

      # detect position on map

      # scale to world-map ratio
      # 480px -> 160px
      crop = cv2.resize(crop, (160, 144))

      # crop the worldmap image to a reasonable size, so we do not have to search so excessively
      # we can only do this if we have a starting position
      validationImage = self._worldmap
      left_corner = (0, 0)
      
      #cv2.imshow('crop', crop)
      #cv2.imshow('img', validationImage)
      #cv2.waitKey(0)

      if not last_position == (-1, -1):
        size = (500, 500)
        left_corner = (last_position[0] - size[0] / 2, last_position[1] - size[1] / 2)
        validationImage = self._worldmap[left_corner[1]:left_corner[1] + size[1], left_corner[0]:left_corner[0] + size[0]]

        #cv2.imshow('crop', crop)
        #cv2.imshow('img', validationImage)
        #cv2.waitKey(0)


      # this should generally not happen, but if it does, we do not know where we are
      # so we're defaulting to the whole worldmap
      # TODO: THIS SHOULD BE REPLACED WITH -> DEFAULTING TO INDOOR MAPS!
      if validationImage.shape < crop.shape:
        validationImage = self._worldmap
        left_corner = (0, 0)

      res = cv2.matchTemplate(validationImage, crop, cv2.TM_CCOEFF_NORMED)
      w, h = crop.shape[::-1]
      min_val, max_val, min_loc, max_loc = cv2.minMaxLoc( res )

      # if we don't find something we're confident in, use the last position
      #if max_val < 0.5 and not last_position == (-1, -1):
      #  self.newEntry(timeStamp, date, last_position)
      #  continue

      # retry until we find a good first match
      if max_val < 0.8:
        validationImage = self._worldmap
        left_corner = (0, 0)

        res = cv2.matchTemplate(validationImage, crop, cv2.TM_CCOEFF_NORMED)
        w, h = crop.shape[::-1]
        min_val, max_val, min_loc, max_loc = cv2.minMaxLoc( res )
        
        print max_val
        if max_val < 0.7:
          print 'hit low'
          continue

      top_left = max_loc
      top_left = (top_left[0] + left_corner[0], top_left[1] + left_corner[1])

      center = (top_left[0] + w / 2, top_left[1] + h / 2)
      bottom_right = (top_left[0] + w, top_left[1] + h)

      last_position = center
      self.newEntry(timeStamp, date, last_position)
      
      if len(self._timeList) > 20:
        self._timeList = []

  def getTimeValues( self, timeCrop ):
    timeValues = []

    for key in self._templatesTime:
      template = self._templatesTime[key]
      w, h = template.shape[::-1]

      res = cv2.matchTemplate(timeCrop, template, cv2.TM_CCOEFF_NORMED)
      threshold = 0.9

      loc = numpy.where( res >= threshold )
      for pt in zip(*loc[::-1]):
        timeValues.append((pt[0], pt[1], key))

    timeValues.sort()
    return timeValues

  def getDateFromTime( self, timeValues ):
    date = {}
    tmp = ''

    for timeValue in timeValues:
      key = timeValue[2]

      if key == 'd':
        date['day'] = tmp.zfill(2)
        tmp = ''

      elif key == 'h':
        date['hour'] = tmp.zfill(2)
        tmp = ''

      elif key == 'm':
        date['minute'] = tmp.zfill(2)
        tmp = ''

      elif key == 's':
        date['second'] = tmp.zfill(2)
        tmp = ''

      else:
        tmp = '{}{}'.format(tmp, key)

    return date

  def getRedDirection( self, redCrop ):
    currentDirection = 'undefined'
    redCrop = cv2.resize(redCrop, (10, 10))
    
    for direction in self._templatesRed:
      template = self._templatesRed[direction]
      template = cv2.resize(template, (10, 10))

      res = cv2.matchTemplate(redCrop, template, cv2.TM_CCOEFF_NORMED)
      min_val, max_val, min_loc, max_loc = cv2.minMaxLoc( res )

      if max_val > 0.55:
        currentDirection = direction
        break

    return currentDirection

  def detectEvents( self, crop ):
    eventValues = []

    detectionAreas = {}
    detectionAreas['moonstone'] = crop.copy() # moonstone
    detectionAreas['dialogue'] = crop[293:435, 0:480] # dialogue
    detectionAreas['optionsMenu'] = crop[3:388, 240:480] # optionsMenu
    detectionAreas['naming'] = crop[98:360, 0:480] # naming

    for key in self._templatesEvent:
      template = self._templatesEvent[key]
      area = detectionAreas[key]

      w, h = template.shape[::-1]
      if area.shape < template.shape:
        break

      res = cv2.matchTemplate(area, template, cv2.TM_CCOEFF_NORMED)
      threshold = 0.95

      loc = numpy.where( res >= threshold )
      for pt in zip(*loc[::-1]):
        _tuple = (pt[0], pt[1], key)
        if not _tuple in eventValues:
          eventValues.append(_tuple)

      # currently there can only be one event at any given timespace
      if len(eventValues) > 0:
        break

    return eventValues

  def newEntry( self, timeStamp, date, position ):
    _tuple = (timeStamp, date, position)

    # we are in a room without outsideworld reference
    # this could only happen, if we start recording inside a building right now
    if position == (-1, -1):
      print 'WHERE ARE WE?\r'
      return

    if not _tuple in self._timeList:
      self._timeList.append(_tuple)
      print '{}\t{},{}\r'.format(str(timeStamp), position[0], position[1]),

      with open('positionData.csv', 'a') as entryFile:
        line = '{};{};{};{};{};{};{}\n'.format(timeStamp, \
                                      date['day'], date['hour'], date['minute'], date['second'], \
                                      position[0], position[1])
        entryFile.write(line)

      with open( 'www/currentPosition', 'w' ) as _file:
        _file.write('{} {}'.format(position[0], position[1]))

      with open( 'www/currentTime', 'w' ) as _file:
        _file.write('{} {} {} {}'.format(date['day'], date['hour'], date['minute'], date['second']))



if __name__ == "__main__":
  timeList = []
  eventList = []
  worldmap = cv2.imread( 'map.png' )

  streamFetcher = StreamAnalysis(timeList, eventList, worldmap)
  mapGenerator = MapGeneration(timeList, eventList, worldmap)

  streamFetcher.start()
  mapGenerator.start()
